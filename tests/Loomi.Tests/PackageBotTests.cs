using System.Net;
using System.Net.Http.Json;
using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Loomi.Telegram;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Xunit;

namespace Loomi.Tests;
/// <summary>What a member hears about their packages in Telegram: that one was given, that one ends soon, that one has ended, and what is left of
/// each under /balance. Each notice goes out once, however often the worker looks.</summary>
public class PackageBotTests
{
    private const long SaraChat = 22, OwnerChat = 11;
    /// <summary>A workspace with a bot, whose Bot API is <see cref="Telegram"/>.</summary>
    private sealed class BotFactory(ITelegramClient? client = null) : AppFactory
    {
        public FakeTelegram Telegram { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Telegram:BotToken", "test-token");
            builder.ConfigureServices(services => { services.RemoveAll<ITelegramClient>(); services.AddSingleton(client ?? Telegram); });
        }
    }
    /// <summary>A Bot API that is down: every call fails the way a network error would.</summary>
    private sealed class DownTelegram : ITelegramClient
    {
        private static Exception Down => new HttpRequestException("unreachable");
        public Task<IReadOnlyList<Update>> GetUpdatesAsync(int offset, CancellationToken ct) => throw Down;
        public Task<int> SendTextAsync(long chat, string text, CancellationToken ct, IReadOnlyList<IReadOnlyList<BotButton>>? buttons = null) => throw Down;
        public Task SendPhotoAsync(long chat, byte[] photo, string name, string? caption, CancellationToken ct, IReadOnlyList<IReadOnlyList<BotButton>>? buttons = null) => throw Down;
        public Task SendDocumentAsync(long chat, byte[] file, string name, string? caption, CancellationToken ct) => throw Down;
        public Task EditTextAsync(long chat, int message, string text, CancellationToken ct) => throw Down;
        public Task DeleteAsync(long chat, int message, CancellationToken ct) => throw Down;
        public Task AnswerAsync(string callback, CancellationToken ct) => throw Down;
        public Task SetCommandsAsync(IReadOnlyList<BotCommandText> commands, CancellationToken ct) => throw Down;
        public Task<byte[]?> DownloadAsync(string fileId, long maxBytes, CancellationToken ct) => throw Down;
        public Task<string?> WhoAmIAsync(string token, CancellationToken ct) => throw Down;
    }
    private static async Task<T> WithDbAsync<T>(AppFactory factory, Func<AppDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
    private static Task<Guid> MemberAsync(AppFactory factory, string name, long? chat) => WithDbAsync(factory, async db =>
    {
        var user = new AppUser { Username = name, NormalizedUsername = name, Role = UserRole.Member };
        db.Users.Add(user);
        db.CreditEntries.Add(new CreditEntry { UserId = user.Id, Amount = 40, Kind = CreditKind.Grant });
        if (chat is { } id) db.TelegramLinks.Add(new TelegramLink { UserId = user.Id, ChatId = id });
        await db.SaveChangesAsync();
        return user.Id;
    });
    private static Task<Guid> AddAsync(AppFactory factory, Guid user, string name, DateTime starts, DateTime ends, PackageKind kind = PackageKind.Daily, int amount = 3, DateTime? endedEarly = null) =>
        WithDbAsync(factory, async db =>
        {
            var package = new CreditPackage { UserId = user, Kind = kind, Amount = amount, Name = name, StartsAt = starts, EndsAt = ends, EndedEarlyAt = endedEarly };
            db.CreditPackages.Add(package);
            await db.SaveChangesAsync();
            return package.Id;
        });
    private static Task<CreditPackage> ReadAsync(AppFactory factory, Guid id) => WithDbAsync(factory, db => db.CreditPackages.AsNoTracking().SingleAsync(p => p.Id == id));
    private static PackageReminderWorker Reminders(AppFactory factory, FakeTelegram bot, BotSettings? settings = null) =>
        new(factory.Services.GetRequiredService<IServiceScopeFactory>(), bot, settings ?? TelegramTests.ConfiguredBot(factory), NullLogger<PackageReminderWorker>.Instance);
    private static TelegramBotWorker Bot(AppFactory factory, FakeTelegram bot) => new(factory.Services.GetRequiredService<IServiceScopeFactory>(), bot,
        factory.Services.GetRequiredService<GenerationEvents>(), Options.Create(new TelegramOptions { BotToken = "test-token", MediaGroupMs = 1500 }),
        Options.Create(new BrowserOptions()), TelegramTests.ConfiguredBot(factory), factory.Services.GetRequiredService<QueueEstimates>(), NullLogger<TelegramBotWorker>.Instance);
    private static Message Text(string text, long chat, string? language) => new() { Chat = new() { Id = chat }, From = new() { Id = 1, FirstName = "u", LanguageCode = language }, Text = text };

    [Fact]
    public async Task A_package_ending_within_three_days_is_announced_once_in_both_languages()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        var sara = await MemberAsync(factory, "sara", SaraChat);
        var omid = await MemberAsync(factory, "omid", null);
        var now = DateTime.UtcNow;
        var ending = await AddAsync(factory, sara, "Gold", now.AddDays(-28), now.AddDays(2).AddHours(-1));
        var later = await AddAsync(factory, sara, "Silver", now.AddDays(-1), now.AddDays(5));
        var unheard = await AddAsync(factory, omid, "Bronze", now.AddDays(-1), now.AddDays(1));
        var notYet = await AddAsync(factory, sara, "Next", now.AddDays(1), now.AddDays(2));
        var worker = Reminders(factory, bot);

        await worker.RunAsync(now, default);
        var (chat, text) = Assert.Single(bot.Texts);
        Assert.Equal(SaraChat, chat);
        var parts = text.Split("\n\n");
        Assert.Equal(2, parts.Length);
        Assert.Contains("«Gold»", parts[0]);
        Assert.Contains("۲ روز دیگر", parts[0]);
        Assert.Contains("Gold", parts[1]);
        Assert.Contains("2 days", parts[1]);
        Assert.NotNull((await ReadAsync(factory, ending)).ReminderSentAt);
        // Nobody to tell is handled too, or it would be looked at again every ten minutes for ever.
        Assert.NotNull((await ReadAsync(factory, unheard)).ReminderSentAt);
        Assert.Null((await ReadAsync(factory, later)).ReminderSentAt);
        Assert.Null((await ReadAsync(factory, notYet)).ReminderSentAt);

        await worker.RunAsync(now.AddMinutes(10), default);
        Assert.Single(bot.Texts);
    }

    [Fact]
    public async Task A_package_that_ran_out_is_announced_once_and_one_stopped_by_hand_or_long_ago_is_not()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        var sara = await MemberAsync(factory, "sara", SaraChat);
        var now = DateTime.UtcNow;
        var ran = await AddAsync(factory, sara, "Gold", now.AddDays(-30), now.AddHours(-1));
        var stopped = await AddAsync(factory, sara, "Silver", now.AddDays(-30), now.AddHours(-1), endedEarly: now.AddHours(-1));
        var old = await AddAsync(factory, sara, "Bronze", now.AddDays(-30), now.AddDays(-3));
        var worker = Reminders(factory, bot);

        await worker.RunAsync(now, default);
        var (chat, text) = Assert.Single(bot.Texts);
        Assert.Equal(SaraChat, chat);
        Assert.Contains("بسته‌ی «Gold» شما تمام شد.", text);
        Assert.Contains("\n\n", text);
        Assert.NotNull((await ReadAsync(factory, ran)).EndNoticeSentAt);
        Assert.Null((await ReadAsync(factory, stopped)).EndNoticeSentAt);
        Assert.Null((await ReadAsync(factory, old)).EndNoticeSentAt);

        await worker.RunAsync(now.AddMinutes(10), default);
        Assert.Single(bot.Texts);
    }

    [Fact]
    public async Task Without_a_bot_nothing_is_marked_so_the_notices_still_go_out_once_one_is_set()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        var sara = await MemberAsync(factory, "sara", SaraChat);
        var now = DateTime.UtcNow;
        var ending = await AddAsync(factory, sara, "Gold", now.AddDays(-28), now.AddDays(1));
        var none = new BotSettings(factory.Services.GetRequiredService<IServiceScopeFactory>(), factory.Services.GetRequiredService<AccountSecrets>(), Options.Create(new TelegramOptions()));
        await Reminders(factory, bot, none).RunAsync(now, default);
        Assert.Empty(bot.Texts);
        Assert.Null((await ReadAsync(factory, ending)).ReminderSentAt);
    }

    [Fact]
    public async Task Balance_lists_each_running_package_with_what_is_left_of_it()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram(); var worker = Bot(factory, bot);
        var sara = await MemberAsync(factory, "sara", SaraChat);
        var now = DateTime.UtcNow;
        var daily = await AddAsync(factory, sara, "Gold", now.AddDays(-1), now.AddDays(10), PackageKind.Daily, 30);
        var credits = await AddAsync(factory, sara, "Silver", now.AddDays(-1), now.AddDays(20), PackageKind.Credits, 100);
        await AddAsync(factory, sara, "Free", now.AddDays(-1), now.AddDays(30), PackageKind.Unlimited, 0);
        await AddAsync(factory, sara, "Gone", now.AddDays(-30), now.AddDays(-1), PackageKind.Daily, 7);
        await WithDbAsync(factory, async db =>
        {
            var project = new ImageProject { UserId = sara, Title = "p" };
            db.Projects.Add(project);
            db.Generations.Add(new Generation { ProjectId = project.Id, UserId = sara, Prompt = "x", Status = RunStatus.Completed, PackageId = daily });
            db.CreditEntries.Add(new CreditEntry { UserId = sara, Amount = -10, Kind = CreditKind.Charge, PackageId = credits });
            return await db.SaveChangesAsync();
        });

        await worker.HandleAsync(Text("/balance", SaraChat, "en"), default);
        var english = bot.LastText;
        Assert.StartsWith("Credits: 40.", english);
        Assert.Contains("Gold", english);
        Assert.Contains("1 of 30", english);
        Assert.Contains("Silver", english);
        Assert.Contains("90 credits", english);
        Assert.Contains("Free", english);
        Assert.Contains("unlimited", english);
        Assert.DoesNotContain("Gone", english);
        // Soonest ending first.
        Assert.True(english.IndexOf("Gold", StringComparison.Ordinal) < english.IndexOf("Silver", StringComparison.Ordinal));

        await worker.HandleAsync(Text("/balance", SaraChat, "fa"), default);
        var persian = bot.LastText;
        Assert.Contains("Gold", persian);
        Assert.Contains("۱ از ۳۰", persian);
        Assert.Contains("۹۰", persian);
        Assert.Contains("نامحدود", persian);
    }

    [Fact]
    public async Task The_owners_balance_reads_as_it_always_did()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram(); var worker = Bot(factory, bot);
        await WithDbAsync(factory, async db =>
        {
            var owner = await db.Users.SingleAsync(u => u.NormalizedUsername == OwnerBootstrap.Username);
            db.TelegramLinks.Add(new TelegramLink { UserId = owner.Id, ChatId = OwnerChat });
            return await db.SaveChangesAsync();
        });
        await worker.HandleAsync(Text("/balance", OwnerChat, "en"), default);
        Assert.Equal(BotMessages.Text("balanceOwner", false, 0), bot.LastText);
    }

    [Fact]
    public async Task Granting_with_notify_tells_the_persons_chat_and_a_bot_that_is_down_does_not_fail_the_grant()
    {
        using (var factory = new BotFactory())
        {
            var sara = await MemberAsync(factory, "sara", SaraChat);
            var owner = await UserAdminTests.OwnerSessionAsync(factory);
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"/api/users/{sara}/packages", new { kind = "Daily", amount = 5, days = 30, notify = true })).StatusCode);
            var (chat, text) = Assert.Single(factory.Telegram.Texts);
            Assert.Equal(SaraChat, chat);
            Assert.Contains("بسته‌ی «روزی ۵ تصویر» برای شما فعال شد تا", text);
            Assert.Contains("\n\n", text);
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"/api/users/{sara}/packages", new { kind = "Daily", amount = 5, days = 30 })).StatusCode);
            Assert.Single(factory.Telegram.Texts);
            // One that waits behind the current one says when it begins.
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"/api/users/{sara}/packages", new { kind = "Daily", amount = 9, days = 30, name = "Later", startAfterCurrent = true, notify = true })).StatusCode);
            Assert.Contains("«Later»", factory.Telegram.LastText);
            Assert.DoesNotContain("فعال شد", factory.Telegram.LastText);
        }
        using (var factory = new BotFactory(new DownTelegram()))
        {
            var sara = await MemberAsync(factory, "sara", SaraChat);
            var owner = await UserAdminTests.OwnerSessionAsync(factory);
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"/api/users/{sara}/packages", new { kind = "Daily", amount = 5, days = 30, notify = true })).StatusCode);
            Assert.Equal(1, await WithDbAsync(factory, db => db.CreditPackages.CountAsync()));
        }
    }
}
