using Loomi.Data;
using Loomi.Services;
using Loomi.Models;
using Microsoft.EntityFrameworkCore;
namespace Loomi.BrowserAutomation;

/// <summary>Binds the callers that predate the pool to one account, so a single browser still has an owner. The dispatcher replaces it.</summary>
public sealed class BrowserAutomationService(BrowserPool pool, IServiceScopeFactory scopes, AccountHealth health)
{
    /// <summary>An install that predates accounts adopts its existing profile folder here, rather than losing the login it holds.</summary>
    public static async Task<BrowserAccount> AccountAsync(AppDbContext db, CancellationToken ct)
    {
        if (!await db.Accounts.AnyAsync(ct))
        {
            db.Accounts.Add(new BrowserAccount { Label = "ChatGPT", ProfileDirectory = "default" });
            await db.SaveChangesAsync(ct);
        }
        return await db.Accounts.OrderBy(a => a.CreatedAt).FirstOrDefaultAsync(a => a.IsEnabled, ct) ?? throw new InvalidOperationException("NoAccount");
    }
    private async Task<BrowserAccount> AccountAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await AccountAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), ct);
    }
    /// <summary>Reports on the first enabled account without creating one: the UI polls this every few seconds.</summary>
    public async Task<ConnectionStatus> StatusAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.Accounts.AsNoTracking().OrderBy(a => a.CreatedAt).FirstOrDefaultAsync(a => a.IsEnabled);
        var status = account == null ? new ConnectionStatus("Disconnected", false, await pool.DesktopUrlAsync()) : await pool.RunAsync(account, session => session.StatusAsync());
        return status with { Stalled = await StalledAsync(db) };
    }
    /// <summary>Why nothing is moving, when something is waiting. Silence here is what makes a parked account look like a broken queue.</summary>
    private async Task<string?> StalledAsync(AppDbContext db)
    {
        if (!await db.Generations.AnyAsync(g => g.Status == RunStatus.Queued)) return null;
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.IsEnabled).ToListAsync();
        if (accounts.Count == 0) return "NoAccount";
        foreach (var account in accounts)
            if (!health.IsSpent(account.Id) && await db.UsedOnAsync(account.Id, default) < account.DailyCap) return null;
        return "AllAccountsSpent";
    }
    public async Task<ConnectionStatus> ConnectAsync(CancellationToken ct) => await pool.RunAsync(await AccountAsync(ct), session => session.ConnectAsync(ct));
    public async Task ResetAsync(CancellationToken ct) => await pool.RunAsync(await AccountAsync(ct), session => session.ResetAsync(ct));
    public async Task NewProjectAsync(CancellationToken ct) => await pool.RunAsync(await AccountAsync(ct), session => session.NewProjectAsync(ct));
}
