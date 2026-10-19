using Loomi.Data;
using Loomi.Models;
using Loomi.Services;
using Microsoft.EntityFrameworkCore;
namespace Loomi.Telegram;
/// <summary>Tells a member on Telegram that a package of theirs ends within three days, and that one has ended, so a renewal can be bought before the
/// free use stops rather than after. Each notice is stamped on the package once handled, so a restart or the next look never sends it twice. In both
/// languages, since a notice has no message from the chat behind it to tell which one it reads. Silent without a bot token.</summary>
public class PackageReminderWorker(IServiceScopeFactory scopes, ITelegramClient client, BotSettings bot, ILogger<PackageReminderWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Soon = TimeSpan.FromDays(3), Recent = TimeSpan.FromDays(2);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Without a bot there is nobody to tell; the owner setting one in the app wakes this.
            var changed = bot.Changed;
            if (!bot.Configured) { try { await changed.WaitAsync(stoppingToken); } catch (OperationCanceledException) { break; } continue; }
            try { await RunAsync(DateTime.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Package reminders failed ({Type})", ex.GetType().Name); }
            try { await Task.Delay(Every, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
    /// <summary>One look at the packages. Public so a test can hand it the time instead of waiting three days. Nothing is stamped while there is no
    /// bot, so the notices still go out once one is set. A package that ran out long ago is past telling, and one the owner stopped by hand is the
    /// owner's to explain.</summary>
    public async Task RunAsync(DateTime utcNow, CancellationToken ct)
    {
        if (!bot.Configured) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var soon = utcNow + Soon;
        foreach (var package in await db.CreditPackages.ActiveAt(utcNow).Where(p => p.EndsAt <= soon && p.ReminderSentAt == null).OrderBy(p => p.EndsAt).ToListAsync(ct))
        {
            await TellAsync(db, client, logger, package.UserId, "packageEnding",
                persian => [package.Name, Days(DaysLeft(package.EndsAt, utcNow), persian), Date(package.EndsAt, persian)], ct);
            // Stamped one by one, so a stop part-way through does not send the ones already sent again.
            package.ReminderSentAt = utcNow;
            await db.SaveChangesAsync(ct);
        }
        var since = utcNow - Recent;
        foreach (var package in await db.CreditPackages.Where(p => p.EndsAt <= utcNow && p.EndsAt > since && p.EndedEarlyAt == null && p.EndNoticeSentAt == null).OrderBy(p => p.EndsAt).ToListAsync(ct))
        {
            await TellAsync(db, client, logger, package.UserId, "packageEnded", _ => [package.Name], ct);
            package.EndNoticeSentAt = utcNow;
            await db.SaveChangesAsync(ct);
        }
    }
    /// <summary>A notice to a person's linked chats, Persian first and English after it. A person with no chat, or switched off, simply hears nothing;
    /// a chat that cannot be reached costs only that chat's copy.</summary>
    public static async Task TellAsync(AppDbContext db, ITelegramClient client, ILogger logger, Guid user, string key, Func<bool, object[]> args, CancellationToken ct)
    {
        var chats = await db.TelegramLinks.AsNoTracking().Where(l => l.UserId == user && db.Users.Any(u => u.Id == l.UserId && !u.IsDisabled)).Select(l => l.ChatId).ToListAsync(ct);
        if (chats.Count == 0) return;
        var text = BotMessages.Text(key, true, args(true)) + "\n\n" + BotMessages.Text(key, false, args(false));
        // Never the exception text: it can carry the bot token from a request URL.
        foreach (var chat in chats)
            try { await client.SendTextAsync(chat, text, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Package notice could not be sent ({Type})", ex.GetType().Name); }
    }
    /// <summary>A day as the reader counts it: the Persian calendar in Persian, on the server's clock, which is theirs.</summary>
    public static string Date(DateTime utc, bool persian) => UsageBot.When(utc, "yyyy/MM/dd", persian);
    /// <summary>Whole days, rounded up: a package with hours left still has "one day" left, never zero.</summary>
    public static int DaysLeft(DateTime endsAt, DateTime utcNow) => Math.Max(1, (int)Math.Ceiling((endsAt - utcNow).TotalDays));
    public static string Days(int n, bool persian) => n == 1 ? BotMessages.Text("oneDay", persian) : BotMessages.Text("days", persian, BotMessages.Number(n, persian));
}
