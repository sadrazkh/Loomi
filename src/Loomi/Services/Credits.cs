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
    public static Task<int> BalanceAsync(this AppDbContext db, Guid user, CancellationToken ct) =>
        db.CreditEntries.AsNoTracking().Where(e => e.UserId == user).SumAsync(e => e.Amount, ct);
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
        // Only work that came to nothing is given back, whoever asks: a delivered image was paid for.
        if (charged is not { CreditCost: > 0, Status: RunStatus.Failed or RunStatus.Cancelled }) return 0;
        if (await db.CreditEntries.AnyAsync(e => e.GenerationId == generation && e.Kind == CreditKind.Refund, ct)) return 0;
        db.CreditEntries.Add(new CreditEntry { UserId = charged.UserId, Amount = charged.CreditCost, Kind = CreditKind.Refund, GenerationId = generation });
        // The pre-check cannot see a row another caller has yet to commit; the unique index is what actually decides.
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is SqliteException { SqliteExtendedErrorCode: SqliteUniqueViolation }) { return 0; }
        return charged.CreditCost;
    }
}
