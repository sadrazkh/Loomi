using System.Globalization;
using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Loomi.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Xunit;

namespace Loomi.Tests;
/// <summary>The owner reads everybody's use in the bot: a summary, one person's figures and requests, their latest pictures, and a nightly summary.
/// A member asking for any of it gets nothing more than anyone sending an unknown command.</summary>
public class UsageBotTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nS8AAAAASUVORK5CYII=");
    private const long OwnerChat = 11, MemberChat = 22;
    private sealed record Setup(AppFactory Factory, FakeTelegram Bot, TelegramBotWorker Worker, Guid Owner, Guid Sara) : IDisposable
    {
        public void Dispose() => Factory.Dispose();
    }
    private static async Task<Setup> SetupAsync()
    {
        var factory = new AppFactory(); var bot = new FakeTelegram();
        var worker = new TelegramBotWorker(factory.Services.GetRequiredService<IServiceScopeFactory>(), bot, factory.Services.GetRequiredService<GenerationEvents>(),
            Options.Create(new TelegramOptions { BotToken = "test-token", MediaGroupMs = 1500 }), Options.Create(new BrowserOptions()), TelegramTests.ConfiguredBot(factory),
            factory.Services.GetRequiredService<QueueEstimates>(), NullLogger<TelegramBotWorker>.Instance);
        var (owner, sara) = await WithDbAsync(factory, async db =>
        {
            var owner = await db.Users.FirstAsync(u => u.NormalizedUsername == OwnerBootstrap.Username);
            var sara = new AppUser { Username = "sara", NormalizedUsername = "sara", Role = UserRole.Member };
            db.Users.Add(sara);
            db.TelegramLinks.AddRange(new TelegramLink { UserId = owner.Id, ChatId = OwnerChat }, new TelegramLink { UserId = sara.Id, ChatId = MemberChat });
            await db.SaveChangesAsync();
            return (owner.Id, sara.Id);
        });
        return new(factory, bot, worker, owner, sara);
    }
    private static async Task<T> WithDbAsync<T>(AppFactory factory, Func<AppDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
    /// <summary>One finished picture and one question answered in words, both made just now.</summary>
    private static Task<Guid> BusyAsync(AppFactory factory, Guid user, DateTime? at = null) => WithDbAsync(factory, async db =>
    {
        var project = new ImageProject { UserId = user, Title = "p" };
        var picture = new Generation { ProjectId = project.Id, UserId = user, Prompt = "a lighthouse at dusk", Status = RunStatus.Completed, CreatedAt = at ?? DateTime.UtcNow };
        picture.LocalImagePath = await factory.Services.GetRequiredService<IImageStorage>().SaveAsync(project.Id, picture.Id, Png, default);
        var words = new Generation { ProjectId = project.Id, UserId = user, Prompt = "which colour?", Status = RunStatus.Answered, Operation = Operation.Reply, CreatedAt = (at ?? DateTime.UtcNow).AddMinutes(1) };
        db.Projects.Add(project); db.Generations.AddRange(picture, words);
        await db.SaveChangesAsync();
        return picture.Id;
    });
    private static Message Text(string text, long chat, string? language = null) => new() { Chat = new() { Id = chat }, From = new() { Id = 1, FirstName = "u", LanguageCode = language }, Text = text };
    private static CallbackQuery Press(string data, long chat, string? language = null) =>
        new() { Id = Guid.NewGuid().ToString("N"), Data = data, From = new() { Id = 1, FirstName = "u", LanguageCode = language }, Message = new() { Chat = new() { Id = chat } } };
    private static DailyReportWorker Nightly(Setup s) => new(s.Factory.Services.GetRequiredService<IServiceScopeFactory>(), s.Bot, TelegramTests.ConfiguredBot(s.Factory),
        Options.Create(new ReportOptions { DailyHour = 23 }), NullLogger<DailyReportWorker>.Instance);
    private static Task<string?> SentOnAsync(Setup s) => WithDbAsync(s.Factory, db => db.Settings.Where(x => x.Key == UsageReport.DailySentKey).Select(x => x.Value).FirstOrDefaultAsync());

    [Fact]
    public async Task Users_shows_the_owner_who_is_busy_and_is_an_unknown_command_to_a_member()
    {
        using var s = await SetupAsync();
        await BusyAsync(s.Factory, s.Sara);

        await s.Worker.HandleAsync(Text("/users", OwnerChat), default);
        var (chat, summary) = s.Bot.Texts[^1];
        Assert.Equal(OwnerChat, chat);
        Assert.Contains("sara — 2 requests, 1 images", summary);
        Assert.Contains($"usr:t{s.Sara:N}", s.Bot.Offered);
        Assert.Equal(["• " + BotMessages.Text("btnPeriodToday", false), "usage:w", "usage:m", "usage:a"],
            s.Bot.TextButtons[^1]![0].Select((b, i) => i == 0 ? b.Text : b.Data));

        var before = s.Bot.Texts.Count;
        await s.Worker.HandleAsync(Text("/users", MemberChat), default);
        Assert.Equal((MemberChat, BotMessages.Text("help", false)), s.Bot.Texts[^1]);
        Assert.Equal(before + 1, s.Bot.Texts.Count);
        Assert.DoesNotContain(s.Bot.TextButtons[^1]!.SelectMany(r => r), b => b.Data.StartsWith("usr:") || b.Data.StartsWith("usage:"));
        Assert.DoesNotContain(TelegramBotWorker.Commands, c => c.Command == "users");
    }

    [Fact]
    public async Task A_period_button_counts_that_period_and_a_quiet_one_says_so()
    {
        using var s = await SetupAsync();
        await BusyAsync(s.Factory, s.Sara, DateTime.UtcNow.AddDays(-3));
        await s.Worker.HandleCallbackAsync(Press("usage:t", OwnerChat), default);
        Assert.Contains(BotMessages.Text("usageNone", false), s.Bot.LastText);
        await s.Worker.HandleCallbackAsync(Press("usage:w", OwnerChat), default);
        Assert.Contains("sara — 2 requests, 1 images", s.Bot.LastText);
        Assert.Contains($"usr:w{s.Sara:N}", s.Bot.Offered);
    }

    [Fact]
    public async Task A_user_button_shows_their_figures_and_requests_and_the_images_button_sends_their_pictures()
    {
        using var s = await SetupAsync();
        var picture = await BusyAsync(s.Factory, s.Sara);

        await s.Worker.HandleCallbackAsync(Press($"usr:t{s.Sara:N}", OwnerChat), default);
        var details = s.Bot.LastText;
        Assert.Contains("sara", details);
        Assert.Contains("✅ «a lighthouse at dusk»", details);
        Assert.Contains("💬 «which colour?»", details);
        // Answered in words is not a picture, so it does not count against the daily quota.
        Assert.Contains("1 / ∞", details);
        Assert.Equal([$"uimg:{s.Sara:N}", "usage:t"], s.Bot.TextButtons[^1]!.SelectMany(r => r).Select(b => b.Data));

        await s.Worker.HandleCallbackAsync(Press($"uimg:{s.Sara:N}", OwnerChat), default);
        var photo = Assert.Single(s.Bot.Photos);
        Assert.Equal(OwnerChat, photo.Chat);
        Assert.Equal(Png, photo.Photo);
        Assert.Contains("a lighthouse at dusk", photo.Caption);
        Assert.Contains($"{picture:N}", photo.Name);

        // The owner has made nothing, and is told so rather than left waiting.
        await s.Worker.HandleCallbackAsync(Press($"uimg:{s.Owner:N}", OwnerChat), default);
        Assert.Equal(BotMessages.Text("usageNoImages", false), s.Bot.LastText);
    }

    [Fact]
    public async Task A_member_pressing_the_owners_buttons_gets_nothing()
    {
        using var s = await SetupAsync();
        await BusyAsync(s.Factory, s.Sara);
        foreach (var data in new[] { "usage:t", $"usr:t{s.Sara:N}", $"uimg:{s.Sara:N}", $"usr:a{s.Owner:N}" })
            await s.Worker.HandleCallbackAsync(Press(data, MemberChat), default);
        Assert.Empty(s.Bot.Texts);
        Assert.Empty(s.Bot.Photos);
        Assert.Equal(4, s.Bot.Answered.Count);
    }

    [Fact]
    public async Task In_Persian_the_details_use_the_Persian_calendar_and_digits()
    {
        using var s = await SetupAsync();
        await BusyAsync(s.Factory, s.Sara, new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
        await s.Worker.HandleCallbackAsync(Press($"usr:a{s.Sara:N}", OwnerChat, "fa"), default);
        var details = s.Bot.LastText;
        Assert.Contains("۱۴۰۵/۰۶/۱۰", details);
        Assert.Contains(BotMessages.Text("periodAll", true), details);
        Assert.DoesNotContain(details, char.IsAsciiDigit);
    }

    [Fact]
    public async Task The_nightly_report_goes_to_the_owner_once_from_the_hour_on()
    {
        using var s = await SetupAsync();
        await BusyAsync(s.Factory, s.Sara);
        var worker = Nightly(s);
        var night = DateTime.Today.AddHours(23);

        await worker.RunAsync(night.AddMinutes(-1), default);
        Assert.Empty(s.Bot.Texts);
        Assert.Null(await SentOnAsync(s));

        await worker.RunAsync(night, default);
        var (chat, text) = Assert.Single(s.Bot.Texts);
        Assert.Equal(OwnerChat, chat);
        Assert.Contains(BotMessages.Text("usageNightly", true, BotMessages.Text("periodToday", true)), text);
        Assert.Contains("sara — ۲", text);
        Assert.Equal(night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), await SentOnAsync(s));

        await worker.RunAsync(night.AddMinutes(30), default);
        Assert.Single(s.Bot.Texts);
    }

    [Fact]
    public async Task A_quiet_day_sends_nothing_but_counts_as_done()
    {
        using var s = await SetupAsync();
        var night = DateTime.Today.AddHours(23);
        await Nightly(s).RunAsync(night, default);
        Assert.Empty(s.Bot.Texts);
        Assert.Equal(night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), await SentOnAsync(s));
    }

    [Fact]
    public async Task The_nightly_report_switched_off_is_not_sent()
    {
        using var s = await SetupAsync();
        await BusyAsync(s.Factory, s.Sara);
        using (var scope = s.Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<UsageReport>().SetDailyEnabledAsync(false, default);
        await Nightly(s).RunAsync(DateTime.Today.AddHours(23), default);
        Assert.Empty(s.Bot.Texts);
    }
}
