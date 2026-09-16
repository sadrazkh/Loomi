using System.Collections.Concurrent;
using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.DTOs;
using Loomi.Models;
using Loomi.Providers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Services;
/// <summary>Spreads the queue over the usable accounts: one run per account at a time, each row given to an account of its own provider and claimed in the database so two accounts can never take the same work.</summary>
public class GenerationWorker(IServiceScopeFactory scopes, ProviderRegistry providers, IImageStorage storage, IHubContext<StatusHub> hub, AccountHealth health, ILogger<GenerationWorker> logger) : BackgroundService
{
    private static readonly string[] Reportable = ["LoginRequired", "VerificationRequired", "GenerationTimeout", "InvalidImage", "ConversationNotSaved", "NoAccount", "NoImageReturned", "QuotaExceeded", "ContentBlocked", "UploadFailed", "InputMissing"];
    /// <summary>Picked files nobody sent are dropped once a day old; checked hourly, so the check itself costs nothing.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
    private DateTime nextSweep = DateTime.MinValue;
    /// <summary>Only the dispatch loop touches this, so the count of runs in flight never needs a lock.</summary>
    private readonly Dictionary<Guid, Task> running = [];
    /// <summary>Runs in flight, so a cancel can reach one that has already left the queue.</summary>
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> InFlight = new();
    /// <summary>True when a run was actually interrupted; a row that never started is cancelled by the caller writing the row.</summary>
    public static bool Interrupt(Guid generation) { if (!InFlight.TryGetValue(generation, out var source)) return false; source.Cancel(); return true; }
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
                if (DateTime.UtcNow >= nextSweep) { await SweepUploadsAsync(stoppingToken); nextSweep = DateTime.UtcNow + SweepInterval; }
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
        var interrupted = await db.Generations.Where(g => g.Status != RunStatus.Queued && g.Status != RunStatus.Completed && g.Status != RunStatus.Failed && g.Status != RunStatus.Cancelled)
            .Select(g => g.Id).ToListAsync(ct);
        await db.Generations.Where(g => interrupted.Contains(g.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, RunStatus.Failed).SetProperty(g => g.ErrorMessage, "Interrupted"), ct);
        // Work the process abandoned charged somebody; it never ran, so the credits go back.
        foreach (var id in interrupted) await db.RefundAsync(id, ct);
        foreach (var project in await db.Projects.ToListAsync(ct))
            project.Status = await db.Generations.AnyAsync(g => g.ProjectId == project.Id && g.Status == RunStatus.Queued, ct) ? "Queued" : "Ready";
        await db.SaveChangesAsync(ct);
    }
    private async Task SweepUploadsAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await SweepUploadsAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), storage, DateTime.UtcNow, ct);
    }
    /// <summary>Drops uploads nothing ever used once they are a day old: a file somebody picked and never sent. One a generation refers to stays as long as the generation does.</summary>
    public static async Task<int> SweepUploadsAsync(AppDbContext db, IImageStorage storage, DateTime now, CancellationToken ct)
    {
        var cutoff = now.AddHours(-24);
        var orphans = await db.Uploads.Where(u => u.CreatedAt < cutoff && !db.GenerationInputs.Any(i => i.UploadId == u.Id)).ToListAsync(ct);
        foreach (var upload in orphans) { storage.Delete(upload.Path); db.Uploads.Remove(upload); }
        await db.SaveChangesAsync(ct);
        return orphans.Count;
    }
    /// <summary>The files to send, in the order recorded. A row queued before inputs existed carries only its parent, which is still sent; a file gone since then fails the run rather than quietly sending fewer.</summary>
    private async Task<List<string>> InputPathsAsync(AppDbContext db, Generation g, Generation? parent, CancellationToken ct)
    {
        if (g.Inputs.Count == 0) return parent?.LocalImagePath is { } path ? [storage.Resolve(path)] : [];
        var uploads = g.Inputs.Where(i => i.UploadId != null).Select(i => i.UploadId!.Value).ToList();
        var sources = g.Inputs.Where(i => i.SourceGenerationId != null).Select(i => i.SourceGenerationId!.Value).ToList();
        var uploadPaths = await db.Uploads.AsNoTracking().Where(u => uploads.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Path, ct);
        var sourcePaths = await db.Generations.AsNoTracking().Where(x => sources.Contains(x.Id) && x.LocalImagePath != null).ToDictionaryAsync(x => x.Id, x => x.LocalImagePath!, ct);
        var paths = new List<string>();
        foreach (var input in g.Inputs.OrderBy(i => i.Order))
        {
            var relative = input.UploadId is { } upload ? uploadPaths.GetValueOrDefault(upload) : sourcePaths.GetValueOrDefault(input.SourceGenerationId!.Value);
            if (relative == null || !File.Exists(storage.Resolve(relative))) throw new InvalidOperationException("InputMissing");
            paths.Add(storage.Resolve(relative));
        }
        return paths;
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
            if (running.ContainsKey(account.Id) || providers.For(account.Provider).IsBusy(account) || !await UsableAsync(db, account, ct)) continue;
            // An account only takes work meant for its own provider; a ChatGPT browser cannot fulfil a Gemini request.
            var index = queue.FindIndex(q => q.Provider == account.Provider);
            if (index < 0) continue;
            var generation = queue[index].Id; queue.RemoveAt(index);
            if (!await ClaimAsync(db, generation, account.Id, ct)) continue;
            account.LastUsedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            running[account.Id] = Task.Run(() => RunAsync(account.Id, generation, ct), ct);
            started = true;
        }
        return started;
    }
    /// <summary>The queue in service order, without the rows whose owner has already spent their day.</summary>
    private static async Task<List<(Guid Id, Provider Provider)>> QueueAsync(AppDbContext db, CancellationToken ct)
    {
        var queued = await db.Generations.AsNoTracking().Where(g => g.Status == RunStatus.Queued).OrderBy(g => g.CreatedAt).Select(g => new { g.Id, g.UserId, g.Provider }).ToListAsync(ct);
        if (queued.Count == 0) return [];
        var owners = queued.Select(g => g.UserId).Distinct().ToList();
        var since = Quota.Midnight();
        // Queued rows are left out on purpose: waiting has cost nothing yet, and counting it would let a user's own queue lock itself out the moment they reached their quota.
        var used = await db.Generations.AsNoTracking().Where(g => owners.Contains(g.UserId) && g.CreatedAt >= since && g.Status != RunStatus.Failed && g.Status != RunStatus.Cancelled && g.Status != RunStatus.Queued)
            .GroupBy(g => g.UserId).Select(x => new { User = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.User, x => x.Count, ct);
        var quotas = await db.Users.AsNoTracking().Where(u => owners.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DailyQuota, ct);
        return queued.Where(g => !Limited(quotas, used, g.UserId)).Select(g => (g.Id, g.Provider)).ToList();
    }
    /// <summary>A daily quota of zero is no limit at all; a positive one is spent against today's non-failed work.</summary>
    private static bool Limited(Dictionary<Guid, int> quotas, Dictionary<Guid, int> used, Guid user) =>
        quotas.TryGetValue(user, out var quota) && quota > 0 && used.GetValueOrDefault(user) >= quota;
    /// <summary>A state only a person can clear parks the account; anything else is attempted, so a restart does not strand the queue waiting for someone to press Connect.</summary>
    private async Task<bool> UsableAsync(AppDbContext db, ProviderAccount account, CancellationToken ct) =>
        !health.IsSpent(account.Id) && providers.For(account.Provider).StateOf(account) is not ("LoginRequired" or "VerificationRequired") && await db.UsedOnAsync(account.Id, ct) < account.DailyCap;
    private async Task<bool> ElsewhereAsync(AppDbContext db, ProviderAccount used, CancellationToken ct)
    {
        // Only the same provider: the parent conversation and the retry both belong to that provider's world.
        foreach (var account in await db.Accounts.AsNoTracking().Where(a => a.IsEnabled && a.Id != used.Id && a.Provider == used.Provider).ToListAsync(ct))
            if (await UsableAsync(db, account, ct)) return true;
        return false;
    }
    private async Task RunAsync(Guid accountId, Guid generationId, CancellationToken stopping)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        InFlight[generationId] = cancel;
        var ct = cancel.Token;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        try
        {
            var account = await db.Accounts.FirstAsync(a => a.Id == accountId, ct);
            var g = await db.Generations.Include(x => x.Project).Include(x => x.Inputs).FirstAsync(x => x.Id == generationId, ct);
            async Task Report(RunStatus status, string? conversation = null)
            {
                g.Status = status; g.Project.Status = status.ToString(); g.Project.UpdatedAt = DateTime.UtcNow;
                // Recorded the moment the chat exists, so a run that later times out still points the owner at the conversation.
                if (conversation != null) { g.ConversationUrl = conversation; g.Project.ConversationUrl = conversation; }
                await db.SaveChangesAsync(ct);
                try { await NotifyAsync(hub, g, ct); } catch (OperationCanceledException) { throw; } catch { /* polling repairs missed notifications */ }
            }
            try
            {
                Generation? parent = g.ParentGenerationId == null ? null : await db.Generations.FindAsync([g.ParentGenerationId.Value], ct);
                var request = new GenerationRequest(account.Provider, g.Operation, g.Prompt, await InputPathsAsync(db, g, parent, ct), parent?.ConversationUrl);
                var result = await providers.For(account.Provider).RunAsync(account, request, Report, ct);
                g.LocalImagePath = await storage.SaveAsync(g.ProjectId, g.Id, result.Image, ct);
                g.ConversationUrl = result.ConversationUrl; g.Project.ConversationUrl = result.ConversationUrl;
                await Report(RunStatus.Completed);
            }
            // A cancelled run is a decision, not a fault, and the row has to say so or it looks like the queue ate it.
            catch (OperationCanceledException) when (cancel.IsCancellationRequested && !stopping.IsCancellationRequested) { await FinishAsync(generationId, RunStatus.Cancelled); return; }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Do not log browser exception text: it can contain URLs, page content or tokens.
                logger.LogWarning("Generation {GenerationId} failed ({Type})", g.Id, ex.GetType().Name);
                var code = ErrorCodeFor(ex);
                // Moving to another account keeps the same row and the same charge; only a real failure gives the credits back.
                if (code != "NoImageReturned" || !await MoveOnAsync(db, g, account, Report, ct)) { g.ErrorMessage = code; await Report(RunStatus.Failed); await db.RefundAsync(g.Id, ct); }
            }
            g.Project.Status = await db.Generations.AnyAsync(x => x.ProjectId == g.ProjectId && x.Status == RunStatus.Queued, ct) ? "Queued" : "Ready";
            await db.SaveChangesAsync(ct);
        }
        finally { InFlight.TryRemove(generationId, out _); }
    }
    /// <summary>Writes an outcome on a fresh context: the run's own context may be mid-cancellation and refuse to save.</summary>
    private async Task FinishAsync(Guid generationId, RunStatus status)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var g = await db.Generations.Include(x => x.Project).Include(x => x.Inputs).FirstOrDefaultAsync(x => x.Id == generationId);
        if (g == null) return;
        g.Status = status;
        g.Project.Status = await db.Generations.AnyAsync(x => x.ProjectId == g.ProjectId && x.Status == RunStatus.Queued) ? "Queued" : "Ready";
        await db.SaveChangesAsync();
        if (status is RunStatus.Failed or RunStatus.Cancelled) await db.RefundAsync(generationId, CancellationToken.None);
        try { await NotifyAsync(hub, g, CancellationToken.None); } catch { /* polling repairs missed notifications */ }
    }
    /// <summary>A reply without an image is this account's image allowance running out, not a broken run. The prompt was answered, so it can never be sent into
    /// that conversation again; a fresh conversation on a different account is the one retry that cannot double up, and without one the user is told why.</summary>
    private async Task<bool> MoveOnAsync(AppDbContext db, Generation g, ProviderAccount account, ReportStatus report, CancellationToken ct)
    {
        health.MarkSpent(account.Id);
        if (!await ElsewhereAsync(db, account, ct)) return false;
        g.AccountId = null; g.ErrorMessage = null; g.ConversationUrl = null;
        // The conversation belongs to the account that ran out, so an edit cannot continue it: the retry keeps the parent image but opens a thread of its own.
        if (g.Operation == Operation.Edit) g.Operation = Operation.Branch;
        await report(RunStatus.Queued);
        return true;
    }
}
