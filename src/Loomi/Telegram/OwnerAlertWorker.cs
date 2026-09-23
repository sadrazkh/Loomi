using Loomi.Data;
using Loomi.Models;
using Loomi.Providers;
using Loomi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace Loomi.Telegram;
/// <summary>Tells the owner on Telegram when the accounts need a person, before a tester notices the queue has stopped. Goes only to chats linked to an
/// owner, in both languages, since the bot learns a chat's language only from a message and an alert has none behind it. Silent without a bot token.</summary>
public class OwnerAlertWorker(IServiceScopeFactory scopes, ProviderRegistry providers, AccountHealth health, GenerationEvents events, ITelegramClient client,
                              IOptions<TelegramOptions> telegram, ILogger<OwnerAlertWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(20);
    private readonly AlertRules rules = new(stallAfter: TimeSpan.FromMinutes(2), quietFor: TimeSpan.FromMinutes(15));

    private bool Enabled => !string.IsNullOrWhiteSpace(telegram.Value.BotToken);
    /// <summary>Listening starts here, before the host moves on: ExecuteAsync runs on a thread of its own, and an outcome published before it got
    /// round to subscribing would be missed.</summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (Enabled) events.Subscribe(OutcomeAsync);
        return base.StartAsync(cancellationToken);
    }
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        events.Unsubscribe(OutcomeAsync);
        return base.StopAsync(cancellationToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckAsync(DateTime.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Owner alert check failed ({Type})", ex.GetType().Name); }
            try { await Task.Delay(Every, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
    /// <summary>Whether anything can take waiting work is judged the way the dispatcher judges it: not spent, not over its cap, not waiting on a person.</summary>
    public async Task CheckAsync(DateTime now, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var views = new List<AlertRules.AccountView>();
        var usable = false;
        foreach (var account in await db.Accounts.AsNoTracking().Where(a => a.IsEnabled).OrderBy(a => a.CreatedAt).ToListAsync(ct))
        {
            var state = providers.For(account.Provider).StateOf(account);
            var spent = health.IsSpent(account.Id);
            var capped = await db.UsedOnAsync(account.Id, ct) >= account.DailyCap;
            views.Add(new(account.Id, account.Label, state, spent, capped, account.DailyCap));
            usable |= !spent && !capped && state is not ("LoginRequired" or "VerificationRequired");
        }
        var queued = await db.Generations.CountAsync(g => g.Status == RunStatus.Queued, ct);
        await SendAsync(db, rules.Observe(views, queued, usable, now), ct);
    }
    private async Task OutcomeAsync(GenerationEvent moved, CancellationToken ct)
    {
        if (rules.Outcome(moved.Status, moved.ErrorMessage) is not { } alert) return;
        using var scope = scopes.CreateScope();
        await SendAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), [alert], ct);
    }
    private async Task SendAsync(AppDbContext db, IReadOnlyList<AlertRules.Alert> alerts, CancellationToken ct)
    {
        if (alerts.Count == 0) return;
        var chats = await db.TelegramLinks.AsNoTracking().Where(l => db.Users.Any(u => u.Id == l.UserId && u.Role == UserRole.Owner && !u.IsDisabled)).Select(l => l.ChatId).ToListAsync(ct);
        foreach (var alert in alerts)
        {
            var text = BotMessages.Text(alert.Key, true, alert.Args) + "\n\n" + BotMessages.Text(alert.Key, false, alert.Args);
            // Never the exception text: it can carry the bot token from a request URL.
            foreach (var chat in chats)
                try { await client.SendTextAsync(chat, text, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Owner alert could not be sent ({Type})", ex.GetType().Name); }
        }
    }
}
