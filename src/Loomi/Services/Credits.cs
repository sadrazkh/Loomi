using Loomi.Data;
using Loomi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Services;
/// <summary>Credits are the sum of a ledger, never a stored column: a balance that is added up cannot drift out of step with what it is made of.</summary>
public static class Credits
{
    /// <summary>SQLITE_CONSTRAINT_UNIQUE, raised by the one-refund-per-generation index.</summary>
    private const int SqliteUniqueViolation = 2067;
    /// <summary>The regular balance. A row made against a Credits package is that package's money, which ends with it, so it is left out here.</summary>
    public static Task<int> BalanceAsync(this AppDbContext db, Guid user, CancellationToken ct) =>
        db.CreditEntries.AsNoTracking().Where(e => e.UserId == user && e.PackageId == null).SumAsync(e => e.Amount, ct);
    /// <summary>Pays for a member's request, inside the caller's transaction. The first source with room pays: an Unlimited package, then a Daily one,
    /// then a Credits one, each soonest-ending first so nothing is left to expire that could have been used, and last the regular balance. A source
    /// pays the whole price or nothing; splitting one request across two would make a refund impossible to put back where it came from.</summary>
    public static async Task ChargeAsync(this AppDbContext db, Generation generation, int price, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var running = (await db.CreditPackages.AsNoTracking().Where(p => p.UserId == generation.UserId).ActiveAt(now).ToListAsync(ct))
            .OrderBy(p => p.EndsAt).ThenBy(p => p.CreatedAt).ToList();
        var allowances = running.Where(p => p.Kind == PackageKind.Unlimited).Concat(running.Where(p => p.Kind == PackageKind.Daily)).ToList();
        var used = await db.UsedTodayAsync(allowances.Select(p => p.Id).ToList(), ct);
        if (allowances.FirstOrDefault(p => (p.Kind == PackageKind.Unlimited && p.Amount == 0) || used.GetValueOrDefault(p.Id) < p.Amount) is { } free)
        {
            generation.PackageId = free.Id;
            return;
        }
        var left = await db.LeftAsync(running, ct);
        if (running.FirstOrDefault(p => p.Kind == PackageKind.Credits && left[p.Id] >= price) is { } package) { Charge(db, generation, price, package.Id); return; }
        if (await db.BalanceAsync(generation.UserId, ct) >= price) { Charge(db, generation, price, null); return; }
        // With a daily allowance running, what ran out is today's share of it: tomorrow brings it back, which is worth more to the person than "no credits".
        throw new InvalidOperationException(allowances.Count > 0 ? "PackageLimitReached" : "InsufficientCredits");
    }
    private static void Charge(AppDbContext db, Generation generation, int price, Guid? package)
    {
        generation.CreditCost = price;
        generation.PackageId = package;
        db.CreditEntries.Add(new CreditEntry { UserId = generation.UserId, Amount = -price, Kind = CreditKind.Charge, GenerationId = generation.Id, PackageId = package });
    }
    /// <summary>The rule for this exact operation wins over the provider's default; a provider nobody priced is free.</summary>
    public static async Task<int> PriceAsync(this AppDbContext db, Provider provider, Operation operation, CancellationToken ct)
    {
        var rules = await db.PricingRules.AsNoTracking().Where(r => r.Provider == provider && (r.Operation == operation || r.Operation == null)).ToListAsync(ct);
        return (rules.FirstOrDefault(r => r.Operation == operation) ?? rules.FirstOrDefault(r => r.Operation == null))?.Cost ?? 0;
    }
    /// <summary>Puts back exactly what a generation was charged, once. Returns the amount returned, or zero when there is nothing to return.</summary>
    public static async Task<int> RefundAsync(this AppDbContext db, Guid generation, CancellationToken ct)
    {
        var charged = await db.Generations.AsNoTracking().Where(g => g.Id == generation).Select(g => new { g.UserId, g.CreditCost, g.Status }).FirstOrDefaultAsync(ct);
        // Only work that delivered no picture is given back, whoever asks: a delivered image was paid for, and an answer in words was not an image.
        if (charged is not { CreditCost: > 0, Status: RunStatus.Failed or RunStatus.Cancelled or RunStatus.Answered }) return 0;
        if (await db.CreditEntries.AnyAsync(e => e.GenerationId == generation && e.Kind == CreditKind.Refund, ct)) return 0;
        // Back to the package that paid while it still runs. Once it has ended the credits would be gone with it, for an expiry the person did not
        // cause, so they go to the regular balance instead.
        var now = DateTime.UtcNow;
        var paid = await db.CreditEntries.AsNoTracking().Where(e => e.GenerationId == generation && e.Kind == CreditKind.Charge).Select(e => e.PackageId).FirstOrDefaultAsync(ct);
        var package = paid is { } id && await db.CreditPackages.AnyAsync(p => p.Id == id && p.EndsAt > now, ct) ? paid : null;
        db.CreditEntries.Add(new CreditEntry { UserId = charged.UserId, Amount = charged.CreditCost, Kind = CreditKind.Refund, GenerationId = generation, PackageId = package });
        // The pre-check cannot see a row another caller has yet to commit; the unique index is what actually decides.
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is SqliteException { SqliteExtendedErrorCode: SqliteUniqueViolation }) { return 0; }
        return charged.CreditCost;
    }
}
