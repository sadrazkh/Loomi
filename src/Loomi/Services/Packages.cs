using Loomi.Data;
using Loomi.Models;
using Loomi.Telegram;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Services;
/// <summary>One package as the page and the API read it. UsedToday counts only for Unlimited and Daily; Remaining is credits left for Credits, today's
/// free images left for Daily, and null for an Unlimited package without a cap. Note is the owner's, and null everywhere else.</summary>
public record PackageDto(Guid Id, PackageKind Kind, string Name, int Amount, DateTime StartsAt, DateTime EndsAt, string State, int UsedToday, int? Remaining,
    DateTime CreatedAt, bool EndedEarly, string? Note);
/// <summary>A package's state is read from its times each time it is asked, like the balance and the quota: nothing flips it at the hour it ends, so
/// nothing can fail to.</summary>
public static class Packages
{
    public const string Active = "Active", Scheduled = "Scheduled", Ended = "Ended";
    /// <summary>Over once its end has passed, waiting before its start, running in between. Ending one by hand moves its end to that moment, so no
    /// flag is needed here.</summary>
    public static string StateOf(CreditPackage p, DateTime now) => now >= p.EndsAt ? Ended : now < p.StartsAt ? Scheduled : Active;
    public static IQueryable<CreditPackage> ActiveAt(this IQueryable<CreditPackage> packages, DateTime now) => packages.Where(p => p.StartsAt <= now && p.EndsAt > now);
    /// <summary>What the owner reads when they left the name blank. In Persian, which is how the people using Loomi read it.</summary>
    public static string DefaultName(PackageKind kind, int amount) => kind switch
    {
        PackageKind.Credits => $"{BotMessages.Number(amount, true)} اعتبار",
        PackageKind.Unlimited => amount == 0 ? "نامحدود" : $"نامحدود، روزی تا {BotMessages.Number(amount, true)}",
        _ => $"روزی {BotMessages.Number(amount, true)} تصویر",
    };
    /// <summary>Today's requests each package paid for, counted the way the daily quota counts, so a run that delivered no picture frees its slot.</summary>
    public static async Task<Dictionary<Guid, int>> UsedTodayAsync(this AppDbContext db, IReadOnlyCollection<Guid> packages, CancellationToken ct)
    {
        if (packages.Count == 0) return [];
        var ids = packages.Select(p => (Guid?)p).ToList();
        return await Quota.Today(db).Where(g => ids.Contains(g.PackageId)).GroupBy(g => g.PackageId!.Value)
            .Select(x => new { x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    }
    /// <summary>What each Credits package has left: what it was given, less what was charged to it, plus what came back to it.</summary>
    public static async Task<Dictionary<Guid, int>> LeftAsync(this AppDbContext db, IReadOnlyCollection<CreditPackage> packages, CancellationToken ct)
    {
        var credits = packages.Where(p => p.Kind == PackageKind.Credits).ToList();
        if (credits.Count == 0) return [];
        var ids = credits.Select(p => (Guid?)p.Id).ToList();
        var moved = await db.CreditEntries.AsNoTracking().Where(e => ids.Contains(e.PackageId)).GroupBy(e => e.PackageId!.Value)
            .Select(x => new { x.Key, Sum = x.Sum(e => e.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        return credits.ToDictionary(p => p.Id, p => p.Amount + moved.GetValueOrDefault(p.Id));
    }
    /// <summary>The packages as they read now, in the order given. The note goes only to the owner's own routes.</summary>
    public static async Task<List<PackageDto>> DescribeAsync(this AppDbContext db, IReadOnlyList<CreditPackage> packages, DateTime now, bool withNote, CancellationToken ct)
    {
        var used = await db.UsedTodayAsync(packages.Where(p => p.Kind != PackageKind.Credits).Select(p => p.Id).ToList(), ct);
        var left = await db.LeftAsync(packages, ct);
        return packages.Select(p =>
        {
            var today = p.Kind == PackageKind.Credits ? 0 : used.GetValueOrDefault(p.Id);
            int? remaining = p.Kind switch
            {
                PackageKind.Credits => left.GetValueOrDefault(p.Id),
                PackageKind.Unlimited when p.Amount == 0 => null,
                _ => Math.Max(0, p.Amount - today),
            };
            return new PackageDto(p.Id, p.Kind, p.Name, p.Amount, Utc(p.StartsAt), Utc(p.EndsAt), StateOf(p, now), today, remaining, Utc(p.CreatedAt),
                p.EndedEarlyAt != null, withNote ? p.Note : null);
        }).ToList();
    }
    /// <summary>A person's own packages as they read them: what runs now, soonest ending first, then what is coming, then what ended in the last
    /// thirty days, newest first. Older ones are history only the owner needs.</summary>
    public static async Task<List<PackageDto>> MineAsync(this AppDbContext db, Guid user, DateTime now, CancellationToken ct)
    {
        var since = now.AddDays(-30);
        var rows = await db.CreditPackages.AsNoTracking().Where(p => p.UserId == user && p.EndsAt > since).ToListAsync(ct);
        var ordered = rows.Where(p => StateOf(p, now) == Active).OrderBy(p => p.EndsAt).ThenBy(p => p.CreatedAt)
            .Concat(rows.Where(p => StateOf(p, now) == Scheduled).OrderBy(p => p.StartsAt).ThenBy(p => p.CreatedAt))
            .Concat(rows.Where(p => StateOf(p, now) == Ended).OrderByDescending(p => p.EndsAt).ThenByDescending(p => p.CreatedAt)).ToList();
        return await db.DescribeAsync(ordered, now, withNote: false, ct);
    }
    /// <summary>SQLite hands times back without their kind. They were written as UTC, and said so they reach a browser as UTC rather than as its own clock.</summary>
    private static DateTime Utc(DateTime at) => DateTime.SpecifyKind(at, DateTimeKind.Utc);
}
