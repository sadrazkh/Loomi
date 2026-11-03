using System.Diagnostics;
using System.Globalization;
using Loomi.Data;
using Loomi.Models;
using Loomi.Services;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Exceptions;
namespace Loomi.Telegram;
/// <summary>The news the bot sends on its own, to bring people back without wearing them out: new templates, an invitation after going quiet, and a word when
/// credits run low. Each kind has the owner's switch and starts off, so a deploy sends nothing. It goes only to the people <see cref="NewsAudience"/> names, only
/// by day, and a person hears one piece of it a day at most. In both languages, since news has no message from the chat behind it to tell which one it reads.
/// A message about to go is stamped first, so whatever goes wrong the worst is a message missed, never one sent twice. Silent without a bot token.</summary>
public class NotificationWorker(IServiceScopeFactory scopes, ITelegramClient client, BotSettings bot, ILogger<NotificationWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(15);
    /// <summary>About twenty a second: Telegram's limit for a bot is thirty, and the bot's real work shares it.</summary>
    public static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan DigestGap = TimeSpan.FromDays(7);
    /// <summary>How long someone who has just heard something is left alone by the rest of the news. Someone quiet and low on credits would otherwise get both in one breath.</summary>
    private static readonly TimeSpan Rest = TimeSpan.FromDays(1);
    private const int FirstHour = 10, EndHour = 20;
    private static readonly BotButton TurnOff = Button("notifyBtnOff", "notifyoff");
    private long lastSend;
    /// <summary>Refused is Telegram saying no to this message, which it would say again; Failed is not having got an answer, which may be different next time.</summary>
    private enum Sent { Delivered, Skipped, Blocked, Refused, Failed }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Without a bot there is nobody to tell; the owner setting one in the app wakes this.
            var changed = bot.Changed;
            if (!bot.Configured) { try { await changed.WaitAsync(stoppingToken); } catch (OperationCanceledException) { break; } continue; }
            try { await RunAsync(DateTime.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("News failed ({Type})", ex.GetType().Name); }
            try { await Task.Delay(Every, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
    /// <summary>One look. Public so a test can hand it the time instead of waiting for a week to pass.</summary>
    public async Task RunAsync(DateTime utcNow, CancellationToken ct)
    {
        if (!bot.Configured) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var growth = scope.ServiceProvider.GetRequiredService<GrowthSettings>();
        var audience = scope.ServiceProvider.GetRequiredService<NewsAudience>();
        // Noticing a top-up sends nothing, so it does not wait for the morning or for the switch: someone topped up at midnight who dips at breakfast is told.
        await audience.ClearToppedUpAsync(utcNow, ct);
        if (utcNow.ToLocalTime().Hour is < FirstHour or >= EndHour) return;
        if (await growth.OnAsync(GrowthSettings.NotifyNewTemplates, ct)) await DigestAsync(db, growth, audience, utcNow, ct);
        // The digest goes to everyone at once, so the day after it is everyone's day of rest. What is held back here has stamped nothing and goes tomorrow.
        var since = utcNow - Rest;
        if (await audience.LastDigestAsync(ct) > since) return;
        if (await growth.OnAsync(GrowthSettings.NotifyLowCredit, ct)) await LowCreditAsync(db, growth, audience, utcNow, since, ct);
        if (await growth.OnAsync(GrowthSettings.NotifyWinBack, ct)) await WinBackAsync(db, audience, utcNow, since, ct);
    }
    /// <summary>Everything waiting, as one photo to each chat. The templates and the digest's date are stamped together before the first message goes: a stop
    /// part-way then costs the people not yet reached this one digest and can never send it to the ones already told, which is the mistake people would notice.
    /// The exception is a run where no answer came from Telegram at all: nobody heard it, so it is as if it never went, and the next look tries again.</summary>
    private async Task DigestAsync(AppDbContext db, GrowthSettings growth, NewsAudience audience, DateTime utcNow, CancellationToken ct)
    {
        if (await audience.LastDigestAsync(ct) is { } last && utcNow - last < DigestGap) return;
        var waiting = await audience.WaitingTemplatesAsync(ct);
        var chats = await audience.Reachable().Select(l => l.ChatId).ToListAsync(ct);
        // With nobody to tell yet, a template is still news to whoever links first; nothing is stamped and the week does not start.
        if (waiting.Count == 0 || chats.Count == 0) return;
        var newest = waiting[0];
        var path = audience.SamplePath(newest);
        var photo = await File.ReadAllBytesAsync(path, ct);
        var name = "sample" + Path.GetExtension(path);
        var caption = Both(persian => waiting.Count == 1 ? BotMessages.Text("notifyNewTemplate", persian, newest.Title)
            : BotMessages.Text("notifyNewTemplates", persian, newest.Title, BotMessages.Number(waiting.Count - 1, persian)));
        IReadOnlyList<IReadOnlyList<BotButton>> buttons = [[Button("notifyBtnTry", $"tpl:{newest.Id:N}")], [TurnOff]];
        var previous = await growth.GetAsync(GrowthSettings.NotifyDigestAt, ct);
        foreach (var template in waiting) template.AnnouncedAt = utcNow;
        // One context in this scope, so this one save writes the templates too, in one transaction.
        await growth.SetAsync(GrowthSettings.NotifyDigestAt, utcNow.ToString("O", CultureInfo.InvariantCulture), ct);
        var delivered = false; var unanswered = false;
        foreach (var chat in chats)
        {
            var sent = await SendAsync(db, audience, chat, c => client.SendPhotoAsync(chat, photo, name, caption, c, buttons), ct);
            delivered |= sent == Sent.Delivered;
            unanswered |= sent == Sent.Failed;
        }
        if (delivered || !unanswered) return;
        foreach (var template in waiting) template.AnnouncedAt = null;
        await growth.SetAsync(GrowthSettings.NotifyDigestAt, previous ?? "", ct);
    }
    private async Task LowCreditAsync(AppDbContext db, GrowthSettings growth, NewsAudience audience, DateTime utcNow, DateTime since, CancellationToken ct)
    {
        var price = await audience.ImagePriceAsync(ct);
        var contact = (await growth.GetAsync(GrowthSettings.SiteContact, ct))?.Trim();
        foreach (var (link, credits) in await audience.RunningLowAsync(utcNow, price, ct))
        {
            if (Heard(link, since)) continue;
            // A balance can be below zero after the owner takes credits back; "minus five left" helps nobody.
            var text = Both(persian => BotMessages.Text("notifyLowCredit", persian, BotMessages.Number(Math.Max(0, credits), persian), BotMessages.Number(price, persian))
                + (string.IsNullOrEmpty(contact) ? "" : "\n" + BotMessages.Text("notifyBuyAt", persian, contact)));
            await NudgeAsync(db, audience, link.ChatId, at => link.LowCreditNudgeAt = at, link.LowCreditNudgeAt, utcNow, text, [[TurnOff]], ct);
        }
    }
    private async Task WinBackAsync(AppDbContext db, NewsAudience audience, DateTime utcNow, DateTime since, CancellationToken ct)
    {
        var text = Both(persian => BotMessages.Text("notifyWinBack", persian));
        foreach (var link in await audience.InvitedBack(utcNow).ToListAsync(ct))
            if (!Heard(link, since)) await NudgeAsync(db, audience, link.ChatId, at => link.LastNudgeAt = at, link.LastNudgeAt, utcNow, text, [[Button("notifyBtnTemplates", "templates")], [TurnOff]], ct);
    }
    private static bool Heard(TelegramLink link, DateTime since) => link.LastNudgeAt > since || link.LowCreditNudgeAt > since;
    /// <summary>One person's message, stamped before it goes so a crash cannot repeat it. If no answer came the stamp is put back for the next look: otherwise an
    /// hour of Telegram being unreachable would silence everyone it touched for a month. If Telegram refused it the stamp stays, or that chat would be asked every quarter hour.</summary>
    private async Task NudgeAsync(AppDbContext db, NewsAudience audience, long chat, Action<DateTime?> stamp, DateTime? before, DateTime utcNow, string text, IReadOnlyList<IReadOnlyList<BotButton>> buttons, CancellationToken ct)
    {
        stamp(utcNow);
        await db.SaveChangesAsync(ct);
        if (await SendAsync(db, audience, chat, c => client.SendTextAsync(chat, text, c, buttons), ct) != Sent.Failed) return;
        stamp(before);
        await db.SaveChangesAsync(ct);
    }
    /// <summary>One message, at the pace, to someone who still wants it: a run can take minutes, and whoever pressed "off" in the meantime meant now. A send that
    /// fails costs only that chat's copy. A 403 is Telegram saying the person blocked the bot or left, and there is no point trying them again.</summary>
    private async Task<Sent> SendAsync(AppDbContext db, NewsAudience audience, long chat, Func<CancellationToken, Task> send, CancellationToken ct)
    {
        if (!await audience.Reachable().AnyAsync(l => l.ChatId == chat, ct)) return Sent.Skipped;
        var wait = Pace - Stopwatch.GetElapsedTime(lastSend);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        lastSend = Stopwatch.GetTimestamp();
        try { await send(ct); return Sent.Delivered; }
        // Never the exception text: it can carry the bot token from a request URL.
        catch (ApiRequestException ex) when (ex.ErrorCode == 403)
        {
            logger.LogInformation("News turned off for a chat that blocked the bot ({Type})", ex.GetType().Name);
            await db.TelegramLinks.Where(l => l.ChatId == chat).ExecuteUpdateAsync(s => s.SetProperty(l => l.NotificationsOff, true), ct);
            return Sent.Blocked;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("News could not be sent ({Type})", ex.GetType().Name);
            return ex is ApiRequestException { ErrorCode: >= 400 and < 500 and not 429 } ? Sent.Refused : Sent.Failed;
        }
    }
    /// <summary>Persian first and English after it, as every notice goes that has no message from the chat behind it.</summary>
    private static string Both(Func<bool, string> say) => say(true) + "\n\n" + say(false);
    /// <summary>A button says both as well: above all the way to turn this off has to be readable by whoever gets it.</summary>
    private static BotButton Button(string key, string data) => new(BotMessages.Text(key, true) + " / " + BotMessages.Text(key, false), data);

    // ---- Turning it off, from the chat. Here so the rules of the news stay in one file; the bot only routes to them. ----

    /// <summary>The way out. Its answer says what still comes, and offers the way back.</summary>
    public static async Task SetOffAsync(AppDbContext db, ITelegramClient client, long chat, bool off, bool persian, CancellationToken ct)
    {
        await db.TelegramLinks.Where(l => l.ChatId == chat).ExecuteUpdateAsync(s => s.SetProperty(l => l.NotificationsOff, off), ct);
        await client.SendTextAsync(chat, BotMessages.Text(off ? "notifyNowOff" : "notifyNowOn", persian), ct,
            [[new BotButton(BotMessages.Text(off ? "notifyBtnOn" : "notifyBtnOff", persian), off ? "notifyon" : "notifyoff")]]);
    }
    /// <summary>What /notify does. An owner never gets the news, so saying it is on or off would be saying something untrue to the one person likely to test it.</summary>
    public static async Task ToggleAsync(AppDbContext db, ITelegramClient client, long chat, AppUser user, bool persian, CancellationToken ct)
    {
        if (user.Role == UserRole.Owner) { await client.SendTextAsync(chat, BotMessages.Text("notifyOwner", persian), ct); return; }
        var off = await db.TelegramLinks.AsNoTracking().Where(l => l.ChatId == chat).Select(l => l.NotificationsOff).FirstOrDefaultAsync(ct);
        await SetOffAsync(db, client, chat, !off, persian, ct);
    }
}
