using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
namespace Loomi.Telegram;
/// <summary>Signing up and coming into the web, in a chat. Kept out of <see cref="TelegramBotWorker"/>, which only hands over what belongs here. Built per
/// update with the worker's own client, so a test's fake is the one that speaks.</summary>
public class SignupBot(ITelegramClient client, AppDbContext db, GrowthSettings growth, IConfiguration config, BotSettings bot, ILogger logger)
{
    public const string Join = "signup", HaveAccount = "haveaccount";
    public async Task<bool> OpenAsync(CancellationToken ct) => bot.Configured && await growth.OnAsync(GrowthSettings.SignupOpen, ct);
    /// <summary>A person in a chat of their own: a group, a channel or another bot never gets an account, and a channel's post has nobody behind it at all.</summary>
    private static bool APerson(ChatType type, User? from) => type == ChatType.Private && from is { IsBot: false };
    /// <summary>What a chat with no account behind it hears: the way in while the owner has sign-up open, and otherwise, for groups and for a chat still
    /// linked to a disabled account, what it has always heard.</summary>
    public async Task StrangerAsync(Message message, bool persian, CancellationToken ct)
    {
        var chat = message.Chat.Id;
        if (!APerson(message.Chat.Type, message.From) || !await OpenAsync(ct) || await db.TelegramLinks.AnyAsync(l => l.ChatId == chat, ct))
        {
            await client.SendTextAsync(chat, BotMessages.Text("linkFirst", persian), ct);
            return;
        }
        var gift = await Signup.GiftImagesAsync(db, growth, ct);
        List<string> lines = [BotMessages.Text("signupWelcome", persian)];
        if (gift > 0) lines.Add(BotMessages.Text("signupGift", persian, BotMessages.Number(gift, persian)));
        lines.Add(BotMessages.Text("signupPrivacy", persian));
        await client.SendTextAsync(chat, string.Join("\n\n", lines), ct,
            [[new BotButton(BotMessages.Text(gift > 0 ? "btnSignup" : "btnSignupPlain", persian), Join)], [new BotButton(BotMessages.Text("btnHaveAccount", persian), HaveAccount)]]);
    }
    /// <summary>The welcome's two buttons. An old welcome can still be pressed after the owner has closed sign-up, so being open is asked again here.</summary>
    public async Task PressAsync(CallbackQuery press, long chat, string data, AppUser? user, bool persian, CancellationToken ct)
    {
        if (data == HaveAccount) { await client.SendTextAsync(chat, BotMessages.Text(user is null ? "signupHaveAccount" : "signupAlready", persian), ct); return; }
        if (!APerson(press.Message!.Chat.Type, press.From) || !await OpenAsync(ct))
        {
            if (user is null) await client.SendTextAsync(chat, BotMessages.Text("linkFirst", persian), ct);
            return;
        }
        var outcome = await Signup.SignUpAsync(db, growth, chat, DateTime.UtcNow, ct);
        var text = outcome.Key == "signupDone"
            ? BotMessages.Text(outcome.Key, persian, BotMessages.Number(outcome.GiftImages, persian), BotMessages.Number(Signup.GiftDays, persian))
            : BotMessages.Text(outcome.Key, persian);
        await client.SendTextAsync(chat, text, ct, outcome.User is null ? null
            : [[new BotButton(BotMessages.Text("btnTemplates", persian), "templates"), new BotButton(BotMessages.Text("btnHelp", persian), "help")]]);
        if (outcome is { Created: true, User: { } made }) await TellOwnersAsync(made.Username, ct);
    }
    /// <summary>Best effort: the account is made and the person answered whatever happens here. In both languages, like the owner's other alerts.</summary>
    private async Task TellOwnersAsync(string username, CancellationToken ct)
    {
        var chats = await db.TelegramLinks.AsNoTracking().Where(l => db.Users.Any(u => u.Id == l.UserId && u.Role == UserRole.Owner && !u.IsDisabled)).Select(l => l.ChatId).ToListAsync(ct);
        var text = BotMessages.Text("signupOwnerNotice", true, username) + "\n\n" + BotMessages.Text("signupOwnerNotice", false, username);
        // Never the exception text: it can carry the bot token from a request URL.
        foreach (var chat in chats)
            try { await client.SendTextAsync(chat, text, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("New account notice could not be sent ({Type})", ex.GetType().Name); }
    }
    /// <summary>A one-time link into the web, for a member. Never for an owner, who has a password: a stolen Telegram chat must not become owner access.
    /// Only in a private chat, since the link signs in whoever opens it. The code sits in the address's fragment, so it never reaches a server log or a
    /// link preview.</summary>
    public async Task WebAsync(Message message, AppUser user, bool persian, CancellationToken ct)
    {
        var chat = message.Chat.Id;
        var site = SiteAddress(config);
        var refusal = user.Role != UserRole.Member ? "webOwner" : message.Chat.Type != ChatType.Private ? "webPrivateOnly" : site is null ? "webNoAddress" : null;
        if (refusal is not null) { await client.SendTextAsync(chat, BotMessages.Text(refusal, persian), ct); return; }
        var code = await LoginLinks.IssueAsync(db, user.Id, DateTime.UtcNow, ct);
        await client.SendTextAsync(chat, BotMessages.Text("webLink", persian, $"{site}/#/link/{code}", BotMessages.Number((long)LoginLinks.Lifetime.TotalMinutes, persian)), ct);
    }
    /// <summary>The site's own address for a link sent from a chat, where no request says what it is: the setting, or else the first allowed host that can
    /// be reached from outside. Null when neither says.</summary>
    public static string? SiteAddress(IConfiguration config)
    {
        if (config["Site:BaseUrl"]?.Trim().TrimEnd('/') is { Length: > 0 } set) return set;
        var host = (config["AllowedHosts"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(h => !h.Contains('*') && !h.Equals("localhost", StringComparison.OrdinalIgnoreCase) && h is not ("127.0.0.1" or "[::1]"));
        return host is null ? null : "https://" + host;
    }
    /// <summary>What to add under "not enough credits" for an account made here, once the owner has said where to buy; nothing for anyone else.</summary>
    public async Task<string> WhereToBuyAsync(AppUser? user, bool persian, CancellationToken ct)
    {
        if (user is not { FromBot: true } || (await growth.GetAsync(GrowthSettings.SiteContact, ct))?.Trim() is not { Length: > 0 } contact) return "";
        return "\n\n" + BotMessages.Text("signupWhereToBuy", persian, contact);
    }
}
