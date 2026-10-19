using Loomi.Data;
using Loomi.Models;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Services;
/// <summary>Usage is counted from the rows themselves: a stored counter drifts, and a midnight job is one more thing that can fail to run.</summary>
public static class Quota
{
    /// <summary>Midnight on the server's own clock rather than UTC's: "today" means the day the people using Loomi are living in.</summary>
    public static DateTime Midnight() => DateTime.Today.ToUniversalTime();
    /// <summary>A run that failed, was cancelled or was answered in words delivered no picture, so none of them is counted. A package's daily
    /// allowance is counted by the same rule, so a failed run gives its slot back there too.</summary>
    internal static IQueryable<Generation> Today(AppDbContext db) { var since = Midnight(); return db.Generations.AsNoTracking().Where(g => g.CreatedAt >= since && g.Status != RunStatus.Failed && g.Status != RunStatus.Cancelled && g.Status != RunStatus.Answered); }
    public static Task<int> UsedByAsync(this AppDbContext db, Guid user, CancellationToken ct) => Today(db).CountAsync(g => g.UserId == user, ct);
    /// <summary>Today's usage per user in one query, so the user list does not count row by row.</summary>
    public static async Task<Dictionary<Guid, int>> UsageAsync(this AppDbContext db, CancellationToken ct) =>
        await Today(db).GroupBy(g => g.UserId).Select(x => new { x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    public static Task<int> UsedOnAsync(this AppDbContext db, Guid account, CancellationToken ct) => Today(db).CountAsync(g => g.AccountId == account, ct);
    /// <summary>Null means no daily limit: a missing user, or a quota of zero. A real number is what is left of a positive quota.</summary>
    public static async Task<int?> RemainingAsync(this AppDbContext db, Guid user, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.Id == user).Select(u => (int?)u.DailyQuota).FirstOrDefaultAsync(ct) is int quota and > 0
            ? Math.Max(0, quota - await db.UsedByAsync(user, ct)) : null;
}
