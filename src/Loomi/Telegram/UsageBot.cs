using System.Globalization;
using Loomi.Data;
using Loomi.Models;
using Loomi.Services;
using Microsoft.EntityFrameworkCore;
using File = System.IO.File;
namespace Loomi.Telegram;
/// <summary>The owner's usage report in a chat: the period summary, one person's figures with their latest requests, and their latest pictures. Kept out
/// of <see cref="TelegramBotWorker"/>, which only checks that the one asking is an owner and hands the press over. Built per update with the worker's own
/// client, so a test's fake is the one that speaks.</summary>
public class UsageBot(ITelegramClient client, UsageReport report, AppDbContext db, IImageStorage images)
{
    /// <summary>How many people a summary names and offers a button for; more would bury the keyboard under names.</summary>
    public const int Listed = 10;
    private const int Latest = 5, LineChars = 60, CaptionChars = 200, MaxText = 4096;
    private static readonly UsagePeriod[] Periods = [UsagePeriod.Today, UsagePeriod.Week, UsagePeriod.Month, UsagePeriod.All];
    /// <summary>Its calendar is the Persian one, so a date reads as the owner counts days; its digits are still Latin, which <see cref="BotMessages.Digits"/> swaps.</summary>
    private static readonly CultureInfo PersianCulture = CultureInfo.GetCultureInfo("fa-IR");

    public async Task SummaryAsync(long chat, UsagePeriod period, bool persian, CancellationToken ct)
    {
        var summary = await report.SummaryAsync(period, ct);
        await client.SendTextAsync(chat, SummaryText(summary, persian), ct, SummaryButtons(summary, persian));
    }
    /// <summary>A pressed report button: <c>usage:p</c> a period, <c>usr:p{id}</c> a person over it, <c>uimg:{id}</c> their pictures. The data came
    /// back from a keyboard this class drew, so anything that does not read is simply ignored.</summary>
    public async Task PressAsync(long chat, string verb, string payload, bool persian, CancellationToken ct)
    {
        switch (verb)
        {
            case "usage" when TryPeriod(payload, out var period): await SummaryAsync(chat, period, persian, ct); break;
            case "usr" when payload.Length > 1 && TryPeriod(payload[..1], out var period) && Guid.TryParseExact(payload[1..], "N", out var user):
                await UserAsync(chat, user, period, persian, ct); break;
            case "uimg" when Guid.TryParseExact(payload, "N", out var user): await ImagesAsync(chat, user, persian, ct); break;
        }
    }
    private async Task UserAsync(long chat, Guid user, UsagePeriod period, bool persian, CancellationToken ct)
    {
        if (await report.UserAsync(user, period, ct) is not { } usage) { await client.SendTextAsync(chat, BotMessages.Text("NotFound", persian), ct); return; }
        var latest = await report.RequestsAsync(user, 0, Latest, ct);
        await client.SendTextAsync(chat, UserText(usage, period, latest, persian), ct,
            [[new BotButton(BotMessages.Text("btnLatestImages", persian), $"uimg:{user:N}"), new BotButton(BotMessages.Text("btnBack", persian), $"usage:{Code(period)}")]]);
    }
    /// <summary>Oldest first, so the newest lands at the bottom of the chat where the owner is already looking. A picture whose file is gone is passed over.</summary>
    private async Task ImagesAsync(long chat, Guid user, bool persian, CancellationToken ct)
    {
        var made = await db.Generations.AsNoTracking().Where(g => g.UserId == user && g.Status == RunStatus.Completed && g.LocalImagePath != null)
            .OrderByDescending(g => g.CreatedAt).Take(Latest).Select(g => new { g.Id, g.CreatedAt, g.Prompt, g.TemplateTitle, Stored = g.LocalImagePath! }).ToListAsync(ct);
        made.Reverse();
        var sent = 0;
        foreach (var g in made)
        {
            var path = images.Resolve(g.Stored);
            if (!File.Exists(path)) continue;
            var said = Said(g.Prompt, g.TemplateTitle);
            var caption = When(g.CreatedAt, "yyyy/MM/dd HH:mm", persian) + (said.Length == 0 ? "" : "\n" + Cut(said, CaptionChars));
            await client.SendPhotoAsync(chat, await File.ReadAllBytesAsync(path, ct), $"loomi-{g.Id:N}{Path.GetExtension(path)}", caption, ct);
            sent++;
        }
        if (sent == 0) await client.SendTextAsync(chat, BotMessages.Text("usageNoImages", persian), ct);
    }

    /// <summary>The same text heads the nightly report, which is why it takes the summary rather than fetching it.</summary>
    public static string SummaryText(UsageSummary summary, bool persian, bool nightly = false)
    {
        string N(long n) => BotMessages.Number(n, persian);
        var busy = Busy(summary);
        List<string> lines =
        [
            BotMessages.Text(nightly ? "usageNightly" : "usageTitle", persian, PeriodName(summary.Period, persian)),
            BotMessages.Text("usageFigures", persian, N(summary.ActiveUsers), N(summary.Requests), N(summary.Images), N(summary.Failed), N(summary.CreditsSpent)),
            "",
            .. busy.Count == 0 ? [BotMessages.Text("usageNone", persian)] : busy.Select(u => BotMessages.Text("usageLine", persian, u.Username, N(u.Requests), N(u.Images))),
        ];
        return Cut(string.Join("\n", lines), MaxText - 1);
    }
    /// <summary>The four periods, the one on show marked, then a button for each person the summary names.</summary>
    public static IReadOnlyList<IReadOnlyList<BotButton>> SummaryButtons(UsageSummary summary, bool persian)
    {
        List<IReadOnlyList<BotButton>> rows =
            [Periods.Select(p => new BotButton((p == summary.Period ? "• " : "") + BotMessages.Text("btnPeriod" + p, persian), $"usage:{Code(p)}")).ToList()];
        rows.AddRange(Busy(summary).Chunk(2).Select(pair =>
            (IReadOnlyList<BotButton>)pair.Select(u => new BotButton($"{u.Username} ({BotMessages.Number(u.Requests, persian)})", $"usr:{Code(summary.Period)}{u.UserId:N}")).ToList()));
        return rows;
    }
    public static string UserText(UserUsage u, UsagePeriod period, IReadOnlyList<UsageItem> latest, bool persian)
    {
        string N(long n) => BotMessages.Number(n, persian);
        var comma = persian ? "، " : ", ";
        var role = BotMessages.Text(u.Role == UserRole.Owner ? "roleOwner" : "roleMember", persian) + (u.IsDisabled ? comma + BotMessages.Text("userDisabled", persian) : "");
        List<string> lines =
        [
            BotMessages.Text("usageUser", persian, u.Username, role, PeriodName(period, persian)),
            BotMessages.Text("usageRequests", persian, N(u.Requests), N(u.Images), N(u.Answers), N(u.Failed), N(u.Cancelled)),
            BotMessages.Text("usageKinds", persian, N(u.NewChats), N(u.FollowUps)),
            BotMessages.Text("usageChannels", persian, N(u.Web), N(u.Bot), N(u.Api)),
            // An owner is never charged, so a balance would be a number with nothing behind it.
            u.CreditsLeft is { } left ? BotMessages.Text("usageCredits", persian, N(u.CreditsSpent), N(left)) : BotMessages.Text("usageCreditsOwner", persian, N(u.CreditsSpent)),
            BotMessages.Text("usageToday", persian, N(u.UsedToday), u.DailyQuota == 0 ? "∞" : N(u.DailyQuota)),
        ];
        if (u.Templates.Count > 0) lines.Add(BotMessages.Text("usageTemplates", persian, string.Join(comma, u.Templates.Select(t => $"{t.Title} ({N(t.Count)})"))));
        lines.Add(BotMessages.Text("usageLastActive", persian, u.LastActive is { } at ? When(at, "yyyy/MM/dd HH:mm", persian) : BotMessages.Text("usageNever", persian)));
        if (latest.Count > 0)
        {
            lines.Add("");
            lines.Add(BotMessages.Text("usageLatest", persian));
            // The latest five are of all time, so one from an earlier day says which day; today's need only the hour.
            lines.AddRange(latest.Select(r => $"• {When(r.CreatedAt, Local(r.CreatedAt).Date == DateTime.Today ? "HH:mm" : "MM/dd HH:mm", persian)} {Icon(r.Status)} «{Cut(Said(r.Prompt, r.TemplateTitle), LineChars)}»"));
        }
        return Cut(string.Join("\n", lines), MaxText - 1);
    }

    private static List<UserUsage> Busy(UsageSummary summary) => summary.Users.Where(u => u.Requests > 0).Take(Listed).ToList();
    /// <summary>One letter per period, in the enum's order, so button data stays well inside Telegram's 64 bytes with a whole id after it.</summary>
    private const string Codes = "twma";
    private static char Code(UsagePeriod period) => Codes[(int)period];
    private static bool TryPeriod(string code, out UsagePeriod period)
    {
        var at = code.Length == 1 ? Codes.IndexOf(code[0]) : -1;
        period = (UsagePeriod)Math.Max(at, 0);
        return at >= 0;
    }
    private static string PeriodName(UsagePeriod period, bool persian) => BotMessages.Text("period" + period, persian);
    private static string Icon(RunStatus status) => status switch
    {
        RunStatus.Completed => "✅", RunStatus.Answered => "💬", RunStatus.Failed => "❌", RunStatus.Cancelled => "⏹", _ => "⏳",
    };
    /// <summary>What the person wrote, on one line; a template used without a note of their own is named instead of showing nothing.</summary>
    private static string Said(string prompt, string? template) => (string.IsNullOrWhiteSpace(prompt) ? template ?? "" : prompt).ReplaceLineEndings(" ").Trim();
    /// <summary>Stored in UTC, read back by SQLite with no kind at all; shown on the server's clock, which is the owner's.</summary>
    private static DateTime Local(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
    private static string When(DateTime utc, string format, bool persian) =>
        BotMessages.Digits(Local(utc).ToString(format, persian ? PersianCulture : CultureInfo.InvariantCulture), persian);
    /// <summary>Never through the middle of a surrogate pair, which Telegram would refuse as malformed text.</summary>
    private static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;
        var end = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..end] + "…";
    }
}
