using Loomi.Data;
using Loomi.Models;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Services;
/// <summary>Usage is counted from the rows themselves: a stored counter drifts, and a midnight job is one more thing that can fail to run.</summary>
public static class Quota
{
    /// <summary>Midnight on the server's own clock rather than UTC's: "today" means the day the people using Loomi are living in.</summary>
    public static DateTime Midnight() => DateTime.Today.ToUniversalTime();
    /// <summary>A run that failed cost nobody anything, so it is not counted.</summary>
    private static IQueryable<Generation> Today(AppDbContext db) { var since = Midnight(); return db.Generations.AsNoTracking().Where(g => g.CreatedAt >= since && g.Status != RunStatus.Failed); }
    public static Task<int> UsedByAsync(this AppDbContext db, Guid user, CancellationToken ct) => Today(db).CountAsync(g => g.UserId == user, ct);
    public static Task<int> UsedOnAsync(this AppDbContext db, Guid account, CancellationToken ct) => Today(db).CountAsync(g => g.AccountId == account, ct);
    /// <summary>Null when the row belongs to no user: work that predates users has nobody's day to spend, and only an owner — who has no limit — can reach it.</summary>
    public static async Task<int?> RemainingAsync(this AppDbContext db, Guid user, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.Id == user).Select(u => (int?)u.DailyQuota).FirstOrDefaultAsync(ct) is { } quota ? Math.Max(0, quota - await db.UsedByAsync(user, ct)) : null;
}
