using System.Collections.Concurrent;
using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.DTOs;
using Loomi.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Services;
/// <summary>Spreads the queue over the usable ChatGPT accounts: one run per account at a time, each row claimed in the database so two accounts can never take the same work.</summary>
public class GenerationWorker(IServiceScopeFactory scopes, BrowserPool pool, IImageStorage storage, IHubContext<StatusHub> hub, ILogger<GenerationWorker> logger) : BackgroundService
{
    private static readonly string[] Reportable = ["LoginRequired", "VerificationRequired", "GenerationTimeout", "InvalidImage", "ConversationNotSaved", "NoAccount", "NoImageReturned", "QuotaExceeded"];
    /// <summary>Only the dispatch loop touches this, so the count of runs in flight never needs a lock.</summary>
    private readonly Dictionary<Guid, Task> running = [];
    /// <summary>Accounts that answered without an image, and the day they did: the site's image allowance is daily, so tomorrow they are worth trying again.</summary>
    private readonly ConcurrentDictionary<Guid, DateTime> spent = new();
    public static string ErrorCodeFor(Exception ex) => ErrorCodeFor(ex.GetType().Name, ex.Message);
    /// <summary>Maps a failure to a safe code. Only recognised messages are shown; anything else could carry page text.</summary>
    public static string ErrorCodeFor(string exceptionType, string message) =>
        exceptionType == "TargetClosedException" ? "BrowserClosed"   // Playwright keeps the type internal, so match by name.
        : Reportable.Contains(message) ? message
        : "AutomationFailed";
    /// <summary>Addressed to the owner of the work. Broadcasting hands every signed-in browser someone else's prompt and image URL.</summary>
    public static Task NotifyAsync(IHubContext<StatusHub> hub, Generation generation, CancellationToken ct) =>
        hub.Clients.User(generation.UserId.ToString()).SendAsync("GenerationUpdated", GenerationDto.From(generation), ct);
    /// <summary>Takes the row only while it is still queued. The affected-row count is the whole of the mutual exclusion.</summary>
    public static async Task<bool> ClaimAsync(AppDbContext db, Guid generation, Guid account, CancellationToken ct) =>
        await db.Generations.Where(g => g.Id == generation && g.Status == RunStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, RunStatus.OpeningBrowser).SetProperty(g => g.AccountId, (Guid?)account), ct) == 1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var account in running.Where(x => x.Value.IsCompleted).Select(x => x.Key).ToList()) running.Remove(account);
                if (!await DispatchAsync(stoppingToken)) await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError("Dispatch failed ({Type})", ex.GetType().Name); await Task.Delay(3000, stoppingToken); }
        }
        // Give the runs in flight a moment to notice; a browser that will not let go must not hold up shutdown, and the next sweep repairs whatever it left behind.
        try { await Task.WhenAll(running.Values).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); } catch { /* every outcome here is repaired on the next start */ }
    }
    /// <summary>Anything mid-run when the process stopped has no browser behind it any more, and its project status has to match.</summary>
    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Generations.Where(g => g.Status != RunStatus.Queued && g.Status != RunStatus.Completed && g.Status != RunStatus.Failed)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, RunStatus.Failed).SetProperty(g => g.ErrorMessage, "Interrupted"), ct);
        foreach (var project in await db.Projects.ToListAsync(ct))
            project.Status = await db.Generations.AnyAsync(g => g.ProjectId == project.Id && g.Status == RunStatus.Queued, ct) ? "Queued" : "Ready";
        await db.SaveChangesAsync(ct);
    }
    private async Task<bool> DispatchAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var queue = await QueueAsync(db, ct);
        if (queue.Count == 0) return false;
        var started = false;
        // Least recently used first, so work spreads across the accounts instead of piling onto whichever one happens to sort first.
        foreach (var account in await db.Accounts.Where(a => a.IsEnabled).OrderBy(a => a.LastUsedAt).ToListAsync(ct))
        {
            if (queue.Count == 0) break;
            if (running.ContainsKey(account.Id) || pool.IsBusy(account.Id) || !await UsableAsync(db, account, ct)) continue;
            var generation = queue[0]; queue.RemoveAt(0);
            if (!await ClaimAsync(db, generation, account.Id, ct)) continue;
            account.LastUsedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            running[account.Id] = Task.Run(() => RunAsync(account.Id, generation, ct), ct);
            started = true;
        }
        return started;
    }
    /// <summary>The queue in service order, without the rows whose owner has already spent their day.</summary>
    private static async Task<List<Guid>> QueueAsync(AppDbContext db, CancellationToken ct)
    {
        var queued = await db.Generations.AsNoTracking().Where(g => g.Status == RunStatus.Queued).OrderBy(g => g.CreatedAt).Select(g => new { g.Id, g.UserId }).ToListAsync(ct);
        if (queued.Count == 0) return [];
        var owners = queued.Select(g => g.UserId).Distinct().ToList();
        var since = Quota.Midnight();
        // Queued rows are left out on purpose: waiting has cost nothing yet, and counting it would let a user's own queue lock itself out the moment they reached their quota.
        var used = await db.Generations.AsNoTracking().Where(g => owners.Contains(g.UserId) && g.CreatedAt >= since && g.Status != RunStatus.Failed && g.Status != RunStatus.Queued)
            .GroupBy(g => g.UserId).Select(x => new { User = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.User, x => x.Count, ct);
        var quotas = await db.Users.AsNoTracking().Where(u => owners.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DailyQuota, ct);
        return queued.Where(g => !quotas.TryGetValue(g.UserId, out var quota) || used.GetValueOrDefault(g.UserId) < quota).Select(g => g.Id).ToList();
    }
    /// <summary>A state only a person can clear parks the account; anything else is attempted, so a restart does not strand the queue waiting for someone to press Connect.</summary>
    private async Task<bool> UsableAsync(AppDbContext db, BrowserAccount account, CancellationToken ct) =>
        !Spent(account.Id) && pool.StateOf(account.Id) is not ("LoginRequired" or "VerificationRequired") && await db.UsedOnAsync(account.Id, ct) < account.DailyCap;
    private bool Spent(Guid account) => spent.TryGetValue(account, out var day) && day == DateTime.Today;
    private async Task<bool> ElsewhereAsync(AppDbContext db, Guid used, CancellationToken ct)
    {
        foreach (var account in await db.Accounts.AsNoTracking().Where(a => a.IsEnabled && a.Id != used).ToListAsync(ct))
            if (await UsableAsync(db, account, ct)) return true;
        return false;
    }
    private async Task RunAsync(Guid accountId, Guid generationId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId, ct);
        var g = await db.Generations.Include(x => x.Project).FirstAsync(x => x.Id == generationId, ct);
        async Task Report(RunStatus status)
        {
            g.Status = status; g.Project.Status = status.ToString(); g.Project.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            try { await NotifyAsync(hub, g, ct); } catch (OperationCanceledException) { throw; } catch { /* polling repairs missed notifications */ }
        }
        try
        {
            Generation? parent = g.ParentGenerationId == null ? null : await db.Generations.FindAsync([g.ParentGenerationId.Value], ct);
            var result = await pool.RunAsync(account, session => session.RunAsync(g, parent?.LocalImagePath is { } path ? storage.Resolve(path) : null, parent?.ConversationUrl, Report, ct));
            g.LocalImagePath = await storage.SaveAsync(g.ProjectId, g.Id, result.Image, ct);
            g.ConversationUrl = result.ConversationUrl; g.Project.ConversationUrl = result.ConversationUrl;
            await Report(RunStatus.Completed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            // Do not log browser exception text: it can contain URLs, page content or tokens.
            logger.LogWarning("Generation {GenerationId} failed ({Type})", g.Id, ex.GetType().Name);
            var code = ErrorCodeFor(ex);
            if (code != "NoImageReturned" || !await MoveOnAsync(db, g, accountId, Report, ct)) { g.ErrorMessage = code; await Report(RunStatus.Failed); }
        }
        g.Project.Status = await db.Generations.AnyAsync(x => x.ProjectId == g.ProjectId && x.Status == RunStatus.Queued, ct) ? "Queued" : "Ready";
        await db.SaveChangesAsync(ct);
    }
    /// <summary>A reply without an image is this account's image allowance running out, not a broken run. The prompt was answered, so it can never be sent into
    /// that conversation again; a fresh conversation on a different account is the one retry that cannot double up, and without one the user is told why.</summary>
    private async Task<bool> MoveOnAsync(AppDbContext db, Generation g, Guid account, Func<RunStatus, Task> report, CancellationToken ct)
    {
        spent[account] = DateTime.Today;
        if (!await ElsewhereAsync(db, account, ct)) return false;
        g.AccountId = null; g.ErrorMessage = null; g.ConversationUrl = null;
        // The conversation belongs to the account that ran out, so an edit cannot continue it: the retry keeps the parent image but opens a thread of its own.
        if (g.Operation == Operation.Edit) g.Operation = Operation.Branch;
        await report(RunStatus.Queued);
        return true;
    }
}
