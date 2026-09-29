using System.Globalization;
using Loomi.Data;
using Loomi.Models;
using Loomi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace Loomi.Telegram;
/// <summary>Sends the owner the day's usage summary once each evening, from <see cref="ReportOptions.DailyHour"/> on the server's clock, which is the
/// owner's. The day it went out is stored rather than held in memory, so a restart after the hour does not send it twice. In Persian, since the owner
/// reads Persian and no message from the chat says otherwise. Silent without a bot token, and on a day nobody used Loomi.</summary>
public class DailyReportWorker(IServiceScopeFactory scopes, ITelegramClient client, BotSettings bot, IOptions<ReportOptions> options, ILogger<DailyReportWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Without a bot there is nobody to tell; the owner setting one in the app wakes this.
            var changed = bot.Changed;
            if (!bot.Configured) { try { await changed.WaitAsync(stoppingToken); } catch (OperationCanceledException) { break; } continue; }
            try { await RunAsync(DateTime.Now, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Nightly report failed ({Type})", ex.GetType().Name); }
            try { await Task.Delay(Every, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
    /// <summary>One look at the clock. Public so a test can hand it the time instead of waiting for the evening.</summary>
    public async Task RunAsync(DateTime localNow, CancellationToken ct)
    {
        if (!bot.Configured || localNow.Hour < options.Value.DailyHour) return;
        using var scope = scopes.CreateScope();
        var report = scope.ServiceProvider.GetRequiredService<UsageReport>();
        var date = localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!await report.DailyEnabledAsync(ct) || await report.SettingAsync(UsageReport.DailySentKey, ct) == date) return;
        var summary = await report.SummaryAsync(UsagePeriod.Today, ct);
        if (summary.Requests > 0)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var chats = await db.TelegramLinks.AsNoTracking().Where(l => db.Users.Any(u => u.Id == l.UserId && u.Role == UserRole.Owner && !u.IsDisabled)).Select(l => l.ChatId).ToListAsync(ct);
            var text = UsageBot.SummaryText(summary, true, nightly: true);
            var buttons = UsageBot.SummaryButtons(summary, true);
            // Never the exception text: it can carry the bot token from a request URL.
            foreach (var chat in chats)
                try { await client.SendTextAsync(chat, text, ct, buttons); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Nightly report could not be sent ({Type})", ex.GetType().Name); }
        }
        // A quiet day is done too: nothing more happens on it that the owner has to hear about tonight.
        await report.SetSettingAsync(UsageReport.DailySentKey, date, ct);
    }
}
