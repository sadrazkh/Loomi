using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Loomi.Telegram;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

namespace Loomi.Tests;
/// <summary>A workspace that knows its own address, as a deployment that sets Site:BaseUrl does.</summary>
public class SiteFactory : AppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Site:BaseUrl", "https://loomi.example.com/");
    }
}
/// <summary>Anyone can try the service from the bot once the owner opens it: one account per chat, a gift to start with, and a one-time way into the web.
/// Until the owner opens it, nothing at all is different from before.</summary>
public class SignupTests
{
    private const long Chat = 6100, OtherChat = 6200, OwnerChat = 6300;
    private sealed record Setup(AppFactory Factory, FakeTelegram Bot, TelegramBotWorker Worker) : IDisposable
    {
        public void Dispose() => Factory.Dispose();
    }
    private static async Task<Setup> SetupAsync(bool open = true, AppFactory? factory = null)
    {
        factory ??= new AppFactory();
        var bot = new FakeTelegram();
        var worker = new TelegramBotWorker(factory.Services.GetRequiredService<IServiceScopeFactory>(), bot, factory.Services.GetRequiredService<GenerationEvents>(),
            Options.Create(new TelegramOptions { BotToken = "test-token", MediaGroupMs = 1500 }), Options.Create(new BrowserOptions()), TelegramTests.ConfiguredBot(factory),
            factory.Services.GetRequiredService<QueueEstimates>(), NullLogger<TelegramBotWorker>.Instance);
        if (open) await SetAsync(factory, GrowthSettings.SignupOpen, "on");
        return new(factory, bot, worker);
    }
    private static async Task SetAsync(AppFactory factory, string key, string value)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<GrowthSettings>().SetAsync(key, value, default);
    }
    private static async Task<T> WithDbAsync<T>(AppFactory factory, Func<AppDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
    private static Message Text(string text, long chat = Chat, ChatType type = ChatType.Private, bool isBot = false, string? language = null) =>
        new() { Chat = new() { Id = chat, Type = type }, From = new() { Id = chat, FirstName = "u", IsBot = isBot, LanguageCode = language }, Text = text };
    private static CallbackQuery Press(string data, long chat = Chat, ChatType type = ChatType.Private, bool isBot = false, string? language = null) =>
        new() { Id = Guid.NewGuid().ToString("N"), Data = data, From = new() { Id = chat, FirstName = "u", IsBot = isBot, LanguageCode = language }, Message = new() { Chat = new() { Id = chat, Type = type } } };
    private static IEnumerable<string> ButtonData(IReadOnlyList<IReadOnlyList<BotButton>>? keys) => keys!.SelectMany(r => r).Select(b => b.Data);
    private static Task<int> BotAccountsAsync(AppFactory factory) => WithDbAsync(factory, db => db.Users.CountAsync(u => u.FromBot));
    private static Task<AppUser> SignedUpAsync(AppFactory factory, long chat = Chat) =>
        WithDbAsync(factory, db => { var name = Signup.UsernameFor(chat); return db.Users.AsNoTracking().SingleAsync(u => u.NormalizedUsername == name); });
    private static async Task<HttpClient> ClientAsync(AppFactory factory)
    {
        var client = factory.CreateClient();
        await RefreshCsrfAsync(client);
        return client;
    }
    private static async Task RefreshCsrfAsync(HttpClient client)
    {
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
    }
    /// <summary>Signed up in the bot and into the web through a fresh link, the only way such an account has in.</summary>
    private static async Task<(AppUser User, HttpClient Client)> MemberOnTheWebAsync(Setup s, long chat = Chat)
    {
        await s.Worker.HandleCallbackAsync(Press("signup", chat), default);
        var user = await SignedUpAsync(s.Factory, chat);
        var code = await WithDbAsync(s.Factory, db => LoginLinks.IssueAsync(db, user.Id, DateTime.UtcNow, default));
        var client = await ClientAsync(s.Factory);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/session/link", new { code })).StatusCode);
        await RefreshCsrfAsync(client);
        return (user, client);
    }
    private static string CodeIn(string text) => Regex.Match(text, "/#/link/([A-Za-z0-9_-]+)").Groups[1].Value;
    private static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // ---- Off, which is what a deploy leaves it at ----

    [Fact]
    public async Task Off_by_default_nothing_changes()
    {
        using var s = await SetupAsync(open: false);
        foreach (var message in new[] { Text("/start"), Text("a lighthouse"), Text("/web") })
        {
            await s.Worker.HandleAsync(message, default);
            Assert.Equal(BotMessages.Text("linkFirst", false), s.Bot.LastText);
            Assert.Null(s.Bot.TextButtons[^1]);
        }
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        Assert.Equal(BotMessages.Text("linkFirst", false), s.Bot.LastText);
        Assert.Equal(0, await BotAccountsAsync(s.Factory));
        Assert.Equal(0, await WithDbAsync(s.Factory, db => db.TelegramLinks.CountAsync()));
    }

    [Fact]
    public async Task Switched_off_again_the_buttons_already_in_a_chat_make_no_account()
    {
        using var s = await SetupAsync();
        await s.Worker.HandleAsync(Text("/start"), default);
        await SetAsync(s.Factory, GrowthSettings.SignupOpen, "off");
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        Assert.Equal(BotMessages.Text("linkFirst", false), s.Bot.LastText);
        Assert.Equal(0, await BotAccountsAsync(s.Factory));
    }

    [Fact]
    public async Task Off_a_linked_chat_sending_start_hears_what_it_always_did()
    {
        using var s = await SetupAsync(open: false);
        await WithDbAsync(s.Factory, async db =>
        {
            var sara = new AppUser { Username = "sara", NormalizedUsername = "sara" };
            db.Users.Add(sara); db.TelegramLinks.Add(new TelegramLink { UserId = sara.Id, ChatId = Chat });
            return await db.SaveChangesAsync();
        });
        await s.Worker.HandleAsync(Text("/start"), default);
        Assert.Equal(BotMessages.Text("linkFirst", false), s.Bot.LastText);
    }

    // ---- The welcome ----

    [Fact]
    public async Task Open_the_welcome_says_what_this_is_and_offers_the_two_buttons()
    {
        using var s = await SetupAsync();
        await s.Worker.HandleAsync(Text("/start", language: "fa-IR"), default);
        Assert.Contains(BotMessages.Number(GrowthSettings.DefaultGiftImages, true), s.Bot.LastText);
        Assert.Contains("ChatGPT", s.Bot.LastText);
        var buttons = s.Bot.TextButtons[^1]!.SelectMany(r => r).ToList();
        Assert.Equal(["signup", "haveaccount"], buttons.Select(b => b.Data));
        Assert.Equal(["شروع و دریافت هدیه", "قبلاً حساب دارم"], buttons.Select(b => b.Text));
        // Reading it signs nobody up.
        Assert.Equal(0, await BotAccountsAsync(s.Factory));

        await s.Worker.HandleCallbackAsync(Press("haveaccount", language: "fa-IR"), default);
        Assert.Equal(BotMessages.Text("signupHaveAccount", true), s.Bot.LastText);
        Assert.Equal(0, await BotAccountsAsync(s.Factory));
    }

    [Fact]
    public async Task The_welcome_tells_the_number_the_owner_chose_and_promises_no_gift_when_there_is_none()
    {
        using var s = await SetupAsync();
        await SetAsync(s.Factory, GrowthSettings.SignupGiftImages, "3");
        await s.Worker.HandleAsync(Text("/start"), default);
        Assert.Contains(BotMessages.Text("signupGift", false, "3"), s.Bot.LastText);

        await SetAsync(s.Factory, GrowthSettings.SignupGiftImages, "0");
        await s.Worker.HandleAsync(Text("/start"), default);
        Assert.DoesNotContain(BotMessages.Text("signupGift", false, "0"), s.Bot.LastText);
        Assert.Equal(BotMessages.Text("btnSignupPlain", false), s.Bot.TextButtons[^1]![0][0].Text);
    }

    [Theory]
    [InlineData(ChatType.Group)]
    [InlineData(ChatType.Supergroup)]
    [InlineData(ChatType.Channel)]
    public async Task A_group_or_a_channel_never_signs_up(ChatType type)
    {
        using var s = await SetupAsync();
        await s.Worker.HandleAsync(Text("/start", type: type), default);
        Assert.Equal(BotMessages.Text("linkFirst", false), s.Bot.LastText);
        Assert.Null(s.Bot.TextButtons[^1]);
        await s.Worker.HandleCallbackAsync(Press("signup", type: type), default);
        Assert.Equal(0, await BotAccountsAsync(s.Factory));
        Assert.Equal(0, await WithDbAsync(s.Factory, db => db.TelegramLinks.CountAsync()));
    }

    [Fact]
    public async Task A_bot_never_signs_up()
    {
        using var s = await SetupAsync();
        await s.Worker.HandleAsync(Text("/start", isBot: true), default);
        Assert.Equal(BotMessages.Text("linkFirst", false), s.Bot.LastText);
        await s.Worker.HandleCallbackAsync(Press("signup", isBot: true), default);
        Assert.Equal(0, await BotAccountsAsync(s.Factory));
        // A post in a channel has nobody behind it at all.
        await s.Worker.HandleAsync(new Message { Chat = new() { Id = Chat, Type = ChatType.Private }, Text = "/start" }, default);
        Assert.Equal(BotMessages.Text("linkFirst", false), s.Bot.LastText);
    }

    [Fact]
    public async Task Start_with_a_code_links_as_it_always_did()
    {
        using var s = await SetupAsync();
        var sara = await WithDbAsync(s.Factory, async db =>
        {
            var user = new AppUser { Username = "sara", NormalizedUsername = "sara" };
            db.Users.Add(user); await db.SaveChangesAsync(); return user.Id;
        });
        var code = await WithDbAsync(s.Factory, db => Linking.IssueAsync(db, sara, DateTime.UtcNow, default));
        await s.Worker.HandleAsync(Text("/start " + code.Code), default);
        Assert.Equal(BotMessages.Text("linked", false, "sara"), s.Bot.LastText);
        Assert.Equal(0, await BotAccountsAsync(s.Factory));
        // Already linked, a bare /start is the help rather than being told the chat is not linked.
        await s.Worker.HandleAsync(Text("/start"), default);
        Assert.Equal(TelegramBotWorker.HelpText(false), s.Bot.LastText);
    }

    [Fact]
    public async Task A_stranger_writing_anything_else_is_shown_the_way_in_too()
    {
        using var s = await SetupAsync();
        await s.Worker.HandleAsync(Text("a lighthouse"), default);
        Assert.Equal(["signup", "haveaccount"], ButtonData(s.Bot.TextButtons[^1]));
        Assert.Equal(0, await WithDbAsync(s.Factory, db => db.Generations.CountAsync()));
    }

    // ---- Signing up ----

    [Fact]
    public async Task The_name_comes_from_the_chat_without_showing_it()
    {
        var name = Signup.UsernameFor(Chat);
        Assert.Equal("tg-" + Sha256Hex(Chat.ToString())[..12], name);
        Assert.Matches("^tg-[0-9a-f]{12}$", name);
        Assert.DoesNotContain(Chat.ToString(), name);
        Assert.Equal(name, Signup.UsernameFor(Chat));
        Assert.NotEqual(name, Signup.UsernameFor(OtherChat));
    }

    [Fact]
    public async Task Pressing_twice_makes_one_account_and_one_gift()
    {
        using var s = await SetupAsync();
        await WithDbAsync(s.Factory, async db =>
        {
            db.TelegramLinks.Add(new TelegramLink { UserId = (await db.Users.SingleAsync()).Id, ChatId = OwnerChat });
            return await db.SaveChangesAsync();
        });
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        Assert.Contains(BotMessages.Number(GrowthSettings.DefaultGiftImages, false), s.Bot.Texts.First(t => t.Chat == Chat).Text);
        Assert.Equal(["templates", "help"], ButtonData(s.Bot.TextButtons[s.Bot.Texts.FindIndex(t => t.Chat == Chat)]));

        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        Assert.Equal(BotMessages.Text("signupAlready", false), s.Bot.Texts.Last(t => t.Chat == Chat).Text);

        var user = await SignedUpAsync(s.Factory);
        Assert.Equal((UserRole.Member, true, "", 0), (user.Role, user.FromBot, user.PasswordHash, user.DailyQuota));
        Assert.Equal(1, await BotAccountsAsync(s.Factory));
        Assert.Equal(user.Id, await WithDbAsync(s.Factory, db => db.TelegramLinks.Where(l => l.ChatId == Chat).Select(l => l.UserId).SingleAsync()));

        var gift = await WithDbAsync(s.Factory, db => db.CreditPackages.AsNoTracking().SingleAsync(p => p.UserId == user.Id));
        Assert.Equal((PackageKind.Credits, true, "هدیه‌ی شروع", GrowthSettings.DefaultGiftImages * 10), (gift.Kind, gift.IsGift, gift.Name, gift.Amount));
        Assert.Null(gift.ReminderSentAt);
        Assert.InRange(gift.EndsAt - gift.StartsAt, TimeSpan.FromDays(29.9), TimeSpan.FromDays(30.1));
        Assert.True(gift.StartsAt <= DateTime.UtcNow);

        // The owner hears of the new account once, by its name and nothing else.
        var told = Assert.Single(s.Bot.Texts, t => t.Chat == OwnerChat);
        Assert.Contains(user.Username, told.Text);
        Assert.DoesNotContain(Chat.ToString(), told.Text);
    }

    [Fact]
    public async Task Two_presses_at_the_same_moment_still_make_one_account()
    {
        using var s = await SetupAsync();
        async Task<SignupOutcome> PressAsync()
        {
            using var scope = s.Factory.Services.CreateScope();
            return await Signup.SignUpAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<GrowthSettings>(), Chat, DateTime.UtcNow, default);
        }
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(PressAsync)));
        Assert.Single(outcomes, o => o.Created);
        Assert.Equal(1, await BotAccountsAsync(s.Factory));
        Assert.Equal(1, await WithDbAsync(s.Factory, db => db.CreditPackages.CountAsync()));
        Assert.Equal(1, await WithDbAsync(s.Factory, db => db.TelegramLinks.CountAsync()));
    }

    [Fact]
    public async Task Unlinking_and_signing_up_again_finds_the_same_account_with_no_second_gift()
    {
        using var s = await SetupAsync();
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        var user = await SignedUpAsync(s.Factory);
        await WithDbAsync(s.Factory, db => db.TelegramLinks.Where(l => l.ChatId == Chat).ExecuteDeleteAsync());

        // The chat has no link now, so it is welcomed like anyone, and the button brings back what it had.
        await s.Worker.HandleAsync(Text("/start"), default);
        Assert.Equal(["signup", "haveaccount"], ButtonData(s.Bot.TextButtons[^1]));
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        Assert.Equal(BotMessages.Text("signupBack", false), s.Bot.LastText);
        Assert.Equal(["templates", "help"], ButtonData(s.Bot.TextButtons[^1]));
        Assert.Equal(user.Id, await WithDbAsync(s.Factory, db => db.TelegramLinks.Where(l => l.ChatId == Chat).Select(l => l.UserId).SingleAsync()));
        Assert.Equal(1, await BotAccountsAsync(s.Factory));
        Assert.Equal(1, await WithDbAsync(s.Factory, db => db.CreditPackages.CountAsync()));
    }

    [Fact]
    public async Task A_disabled_account_is_not_brought_back()
    {
        using var s = await SetupAsync();
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        var user = await SignedUpAsync(s.Factory);
        await WithDbAsync(s.Factory, db => db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsDisabled, true)));
        // Still linked, the chat is not offered a way round.
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        Assert.Equal(BotMessages.Text("signupDisabled", false), s.Bot.LastText);
        await WithDbAsync(s.Factory, db => db.TelegramLinks.ExecuteDeleteAsync());
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        Assert.Equal(BotMessages.Text("signupDisabled", false), s.Bot.LastText);
        Assert.Equal(0, await WithDbAsync(s.Factory, db => db.TelegramLinks.CountAsync()));
        Assert.Equal(1, await BotAccountsAsync(s.Factory));
    }

    [Fact]
    public async Task An_account_since_linked_to_another_chat_is_not_taken_from_it()
    {
        using var s = await SetupAsync();
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        var user = await SignedUpAsync(s.Factory);
        await WithDbAsync(s.Factory, db => db.TelegramLinks.Where(l => l.ChatId == Chat).ExecuteUpdateAsync(x => x.SetProperty(l => l.ChatId, OtherChat)));
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        Assert.Equal(BotMessages.Text("linkUserTaken", false), s.Bot.LastText);
        Assert.Equal(OtherChat, await WithDbAsync(s.Factory, db => db.TelegramLinks.Where(l => l.UserId == user.Id).Select(l => l.ChatId).SingleAsync()));
    }

    [Fact]
    public async Task The_daily_limit_holds_and_a_new_day_opens_again()
    {
        using var s = await SetupAsync();
        await SetAsync(s.Factory, GrowthSettings.SignupDailyLimit, "1");
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        Assert.Equal(1, await BotAccountsAsync(s.Factory));
        await s.Worker.HandleCallbackAsync(Press("signup", OtherChat), default);
        Assert.Equal((OtherChat, BotMessages.Text("signupFull", false)), s.Bot.Texts[^1]);
        Assert.Equal(1, await BotAccountsAsync(s.Factory));
        Assert.Equal(1, await WithDbAsync(s.Factory, db => db.TelegramLinks.CountAsync()));

        // The chat that is full out can still come back to what it already has: the limit is on new accounts only.
        await WithDbAsync(s.Factory, db => db.TelegramLinks.Where(l => l.ChatId == Chat).ExecuteDeleteAsync());
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        Assert.Equal(BotMessages.Text("signupBack", false), s.Bot.LastText);

        var yesterday = Quota.Midnight().AddHours(-1);
        await WithDbAsync(s.Factory, db => db.Users.Where(u => u.FromBot).ExecuteUpdateAsync(x => x.SetProperty(u => u.CreatedAt, yesterday)));
        await s.Worker.HandleCallbackAsync(Press("signup", OtherChat), default);
        Assert.Equal(2, await BotAccountsAsync(s.Factory));
    }

    [Fact]
    public async Task A_limit_of_zero_lets_nobody_in()
    {
        using var s = await SetupAsync();
        await SetAsync(s.Factory, GrowthSettings.SignupDailyLimit, "0");
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        Assert.Equal(BotMessages.Text("signupFull", false), s.Bot.LastText);
        Assert.Equal(0, await BotAccountsAsync(s.Factory));
    }

    [Fact]
    public async Task With_images_free_or_no_gift_chosen_no_package_is_made()
    {
        using var s = await SetupAsync();
        await WithDbAsync(s.Factory, db => db.PricingRules.ExecuteUpdateAsync(x => x.SetProperty(r => r.Cost, 0)));
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        Assert.Equal(BotMessages.Text("signupDonePlain", false), s.Bot.LastText);
        await WithDbAsync(s.Factory, db => db.PricingRules.ExecuteUpdateAsync(x => x.SetProperty(r => r.Cost, 10)));
        await SetAsync(s.Factory, GrowthSettings.SignupGiftImages, "0");
        await s.Worker.HandleCallbackAsync(Press("signup", OtherChat), default);
        Assert.Equal(2, await BotAccountsAsync(s.Factory));
        Assert.Equal(0, await WithDbAsync(s.Factory, db => db.CreditPackages.CountAsync()));
    }

    // ---- /web ----

    [Fact]
    public async Task Web_gives_a_member_a_link_that_is_kept_only_as_its_hash()
    {
        using var s = await SetupAsync(factory: new SiteFactory());
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        await s.Worker.HandleAsync(Text("/web"), default);
        Assert.Contains("https://loomi.example.com/#/link/", s.Bot.LastText);
        var code = CodeIn(s.Bot.LastText);
        Assert.Equal(43, code.Length);

        var row = await WithDbAsync(s.Factory, db => db.LoginLinks.AsNoTracking().SingleAsync());
        Assert.Equal(Sha256Hex(code), row.Hash);
        Assert.NotEqual(code, row.Hash);
        Assert.Null(row.UsedAt);
        Assert.InRange(row.ExpiresAt - DateTime.UtcNow, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10));

        // A second one takes the first's place, so only the last link sent can work.
        await s.Worker.HandleAsync(Text("/web"), default);
        var second = CodeIn(s.Bot.LastText);
        Assert.NotEqual(code, second);
        Assert.Equal(Sha256Hex(second), await WithDbAsync(s.Factory, db => db.LoginLinks.Select(l => l.Hash).SingleAsync()));
    }

    [Fact]
    public async Task Web_never_gives_an_owner_a_link()
    {
        using var s = await SetupAsync(factory: new SiteFactory());
        await WithDbAsync(s.Factory, async db =>
        {
            db.TelegramLinks.Add(new TelegramLink { UserId = (await db.Users.SingleAsync()).Id, ChatId = Chat });
            return await db.SaveChangesAsync();
        });
        await s.Worker.HandleAsync(Text("/web"), default);
        Assert.Equal(BotMessages.Text("webOwner", false), s.Bot.LastText);
        Assert.Equal(0, await WithDbAsync(s.Factory, db => db.LoginLinks.CountAsync()));
    }

    [Fact]
    public async Task Web_in_a_group_gives_no_link_for_the_whole_group_to_use()
    {
        using var s = await SetupAsync(factory: new SiteFactory());
        await WithDbAsync(s.Factory, async db =>
        {
            var sara = new AppUser { Username = "sara", NormalizedUsername = "sara" };
            db.Users.Add(sara); db.TelegramLinks.Add(new TelegramLink { UserId = sara.Id, ChatId = -500 });
            return await db.SaveChangesAsync();
        });
        await s.Worker.HandleAsync(Text("/web", -500, ChatType.Group), default);
        Assert.Equal(BotMessages.Text("webPrivateOnly", false), s.Bot.LastText);
        Assert.Equal(0, await WithDbAsync(s.Factory, db => db.LoginLinks.CountAsync()));
    }

    [Fact]
    public async Task Web_without_an_address_says_so_and_makes_no_link()
    {
        using var s = await SetupAsync();
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        await s.Worker.HandleAsync(Text("/web"), default);
        Assert.Equal(BotMessages.Text("webNoAddress", false), s.Bot.LastText);
        Assert.Equal(0, await WithDbAsync(s.Factory, db => db.LoginLinks.CountAsync()));
    }

    [Theory]
    [InlineData("https://a.example/", "b.example", "https://a.example")]
    [InlineData(null, "*;localhost;127.0.0.1;b.example;c.example", "https://b.example")]
    [InlineData(null, "*.example.com;LOCALHOST; b.example ", "https://b.example")]
    [InlineData(null, "loomi.platform.irnetfree.info", "https://loomi.platform.irnetfree.info")]
    [InlineData(null, "localhost;127.0.0.1", null)]
    [InlineData(null, "*", null)]
    [InlineData("  ", null, null)]
    public void The_web_address_comes_from_the_setting_or_the_first_real_allowed_host(string? baseUrl, string? allowed, string? expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Site:BaseUrl"] = baseUrl, ["AllowedHosts"] = allowed }).Build();
        Assert.Equal(expected, SignupBot.SiteAddress(config));
    }

    [Fact]
    public async Task Web_is_in_the_help_and_in_the_menu()
    {
        using var s = await SetupAsync();
        foreach (var persian in new[] { false, true }) Assert.Contains("/web", TelegramBotWorker.HelpText(persian));
        var web = Assert.Single(TelegramBotWorker.Commands, c => c.Command == "web");
        Assert.True(BotMessages.LooksPersian(web.Fa) && !BotMessages.LooksPersian(web.En));
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        await s.Worker.HandleCallbackAsync(Press("help"), default);
        Assert.Contains("/web", s.Bot.LastText);
    }

    // ---- The link, on the web ----

    [Fact]
    public async Task A_link_signs_in_once_and_says_so_when_it_cannot()
    {
        using var s = await SetupAsync();
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        var user = await SignedUpAsync(s.Factory);
        var code = await WithDbAsync(s.Factory, db => LoginLinks.IssueAsync(db, user.Id, DateTime.UtcNow, default));

        using var first = await ClientAsync(s.Factory);
        var signedIn = await first.PostAsJsonAsync("/api/session/link", new { code });
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        var session = await first.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.True(session.GetProperty("authenticated").GetBoolean());
        Assert.Equal(user.Username, session.GetProperty("username").GetString());
        Assert.False(session.GetProperty("hasPassword").GetBoolean());
        Assert.True(session.GetProperty("fromBot").GetBoolean());
        Assert.NotNull(await WithDbAsync(s.Factory, db => db.LoginLinks.Select(l => l.UsedAt).SingleAsync()));

        using var second = await ClientAsync(s.Factory);
        await AssertRefusedAsync(second, code);
    }

    private static async Task AssertRefusedAsync(HttpClient client, object? code)
    {
        var refused = await client.PostAsJsonAsync("/api/session/link", new { code });
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("LinkExpired", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task A_link_that_expired_or_never_was_is_refused_the_same_way()
    {
        using var s = await SetupAsync();
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        var user = await SignedUpAsync(s.Factory);
        var stale = await WithDbAsync(s.Factory, db => LoginLinks.IssueAsync(db, user.Id, DateTime.UtcNow.AddMinutes(-11), default));
        using var client = await ClientAsync(s.Factory);
        await AssertRefusedAsync(client, stale);
        await AssertRefusedAsync(client, "not-a-code-of-ours");
        await AssertRefusedAsync(client, null);
    }

    [Fact]
    public async Task A_link_of_a_disabled_account_or_of_one_made_owner_since_is_refused()
    {
        using var s = await SetupAsync();
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        var user = await SignedUpAsync(s.Factory);
        using var client = await ClientAsync(s.Factory);

        var code = await WithDbAsync(s.Factory, db => LoginLinks.IssueAsync(db, user.Id, DateTime.UtcNow, default));
        await WithDbAsync(s.Factory, db => db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsDisabled, true)));
        await AssertRefusedAsync(client, code);
        await WithDbAsync(s.Factory, db => db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsDisabled, false).SetProperty(u => u.Role, UserRole.Owner)));
        // A link asked for while still a member must not become owner access: a stolen chat would be enough for it.
        code = await WithDbAsync(s.Factory, db => LoginLinks.IssueAsync(db, user.Id, DateTime.UtcNow, default));
        await AssertRefusedAsync(client, code);
    }

    [Fact]
    public async Task The_link_route_stands_behind_csrf_and_the_login_limit()
    {
        using var factory = new AppFactory();
        using var bare = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await bare.PostAsJsonAsync("/api/session/link", new { code = "x" })).StatusCode);

        using var client = await ClientAsync(factory);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 7; i++) codes.Add((await client.PostAsJsonAsync("/api/session/link", new { code = "x" + i })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, codes[0]);
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[^1]);
    }

    // ---- Passwords ----

    [Fact]
    public async Task An_account_with_no_password_cannot_be_signed_into_with_one_and_fails_cleanly()
    {
        using var s = await SetupAsync();
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        var user = await SignedUpAsync(s.Factory);
        using var client = await ClientAsync(s.Factory);
        foreach (var password in new[] { "anything-at-all", "x" })
        {
            var refused = await client.PostAsJsonAsync("/api/session/login", new { username = user.Username, password });
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("InvalidCredentials", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }
        var service = s.Factory.Services.GetRequiredService<PasswordService>();
        Assert.False(service.Verify(user, ""));
        Assert.False(service.Verify(user, "anything-at-all"));
    }

    [Fact]
    public async Task An_account_with_no_password_sets_one_without_a_current_one_and_then_needs_it()
    {
        using var s = await SetupAsync();
        var (user, client) = await MemberOnTheWebAsync(s);
        using (client)
        {
            var weak = await client.PostAsJsonAsync("/api/session/password", new { newPassword = "short" });
            Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/session/password", new { newPassword = "a-brand-new-password" })).StatusCode);
            Assert.True((await client.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("hasPassword").GetBoolean());

            // From here on it is an account like any other.
            var again = await client.PostAsJsonAsync("/api/session/password", new { newPassword = "another-new-password" });
            Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
            Assert.Equal("InvalidCurrentPassword", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }
        using var fresh = await ClientAsync(s.Factory);
        Assert.Equal(HttpStatusCode.OK, (await fresh.PostAsJsonAsync("/api/session/login", new { username = user.Username, password = "a-brand-new-password" })).StatusCode);
    }

    [Fact]
    public async Task A_token_cannot_set_the_password_of_an_account_with_none()
    {
        using var s = await SetupAsync();
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        var user = await SignedUpAsync(s.Factory);
        var (token, prefix, hash) = ApiTokens.Mint();
        await WithDbAsync(s.Factory, async db => { db.ApiTokens.Add(new ApiToken { UserId = user.Id, Name = "t", Prefix = prefix, Hash = hash }); return await db.SaveChangesAsync(); });
        using var api = s.Factory.CreateClient(new() { HandleCookies = false });
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var refused = await api.PostAsJsonAsync("/api/session/password", new { newPassword = "a-brand-new-password" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("", (await SignedUpAsync(s.Factory)).PasswordHash);
    }

    [Fact]
    public async Task An_account_made_by_hand_still_proves_its_current_password()
    {
        using var factory = new AppFactory();
        using var owner = await UserAdminTests.OwnerSessionAsync(factory);
        var session = await owner.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.True(session.GetProperty("hasPassword").GetBoolean());
        Assert.False(session.GetProperty("fromBot").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.PostAsJsonAsync("/api/session/password", new { newPassword = "a-brand-new-password" })).StatusCode);
    }

    // ---- The owner's card ----

    [Fact]
    public async Task The_settings_are_the_owners_and_read_as_their_defaults_until_set()
    {
        using var s = await SetupAsync(open: false);
        using var anonymous = await ClientAsync(s.Factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/signup/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/signup/settings", new { open = true })).StatusCode);

        using var owner = await UserAdminTests.OwnerSessionAsync(s.Factory);
        var read = await owner.GetFromJsonAsync<JsonElement>("/api/signup/settings");
        Assert.False(read.GetProperty("open").GetBoolean());
        Assert.Equal(GrowthSettings.DefaultGiftImages, read.GetProperty("giftImages").GetInt32());
        Assert.Equal(GrowthSettings.DefaultSignupDailyLimit, read.GetProperty("dailyLimit").GetInt32());
        Assert.Equal(0, read.GetProperty("today").GetInt32());

        var put = await owner.PutAsJsonAsync("/api/signup/settings", new { open = true, giftImages = 2, dailyLimit = 7 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var kept = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(kept.GetProperty("open").GetBoolean());
        Assert.Equal((2, 7), (kept.GetProperty("giftImages").GetInt32(), kept.GetProperty("dailyLimit").GetInt32()));

        // The bot obeys what was just saved, and the count moves.
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        var gift = await WithDbAsync(s.Factory, db => db.CreditPackages.AsNoTracking().SingleAsync());
        Assert.Equal(20, gift.Amount);
        Assert.Equal(1, (await owner.GetFromJsonAsync<JsonElement>("/api/signup/settings")).GetProperty("today").GetInt32());

        using var member = (await MemberOnTheWebAsync(s, OtherChat)).Client;
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/signup/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync("/api/signup/settings", new { open = false })).StatusCode);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(51, 10)]
    [InlineData(5, -1)]
    [InlineData(5, 1001)]
    public async Task Settings_out_of_range_are_refused_and_nothing_is_kept(int gift, int limit)
    {
        using var factory = new AppFactory();
        using var owner = await UserAdminTests.OwnerSessionAsync(factory);
        var refused = await owner.PutAsJsonAsync("/api/signup/settings", new { open = true, giftImages = gift, dailyLimit = limit });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.False((await owner.GetFromJsonAsync<JsonElement>("/api/signup/settings")).GetProperty("open").GetBoolean());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 1000)]
    public async Task The_edges_of_the_range_are_allowed(int gift, int limit)
    {
        using var factory = new AppFactory();
        using var owner = await UserAdminTests.OwnerSessionAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/signup/settings", new { open = true, giftImages = gift, dailyLimit = limit })).StatusCode);
    }

    [Fact]
    public async Task The_owners_list_tells_who_came_from_the_bot_and_deleting_one_leaves_no_link_behind()
    {
        using var s = await SetupAsync(factory: new SiteFactory());
        await s.Worker.HandleCallbackAsync(Press("signup"), default);
        await s.Worker.HandleAsync(Text("/web"), default);
        using var owner = await UserAdminTests.OwnerSessionAsync(s.Factory);
        var users = await owner.GetFromJsonAsync<JsonElement>("/api/users");
        var made = users.EnumerateArray().Single(u => u.GetProperty("fromBot").GetBoolean());
        Assert.Equal(Signup.UsernameFor(Chat), made.GetProperty("username").GetString());
        Assert.Single(users.EnumerateArray(), u => !u.GetProperty("fromBot").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/users/{made.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(0, await WithDbAsync(s.Factory, db => db.LoginLinks.CountAsync()));
    }

    // ---- Running out ----

    [Fact]
    public async Task A_bot_made_account_that_runs_out_is_told_where_to_buy_once_the_owner_has_said()
    {
        using var s = await SetupAsync();
        await SetAsync(s.Factory, GrowthSettings.SignupGiftImages, "0");
        await s.Worker.HandleCallbackAsync(Press("signup"), default);

        await s.Worker.HandleAsync(Text("a lighthouse"), default);
        Assert.Equal(BotMessages.Text("InsufficientCredits", false), s.Bot.LastText);

        await SetAsync(s.Factory, GrowthSettings.SiteContact, "@loomi_sales");
        await s.Worker.HandleAsync(Text("a lighthouse"), default);
        Assert.StartsWith(BotMessages.Text("InsufficientCredits", false), s.Bot.LastText);
        Assert.Contains("@loomi_sales", s.Bot.LastText);

        var user = await SignedUpAsync(s.Factory);
        await WithDbAsync(s.Factory, async db =>
        {
            var now = DateTime.UtcNow;
            db.CreditPackages.Add(new CreditPackage { UserId = user.Id, Kind = PackageKind.Daily, Name = "daily", Amount = 0, StartsAt = now.AddMinutes(-1), EndsAt = now.AddDays(1) });
            return await db.SaveChangesAsync();
        });
        await s.Worker.HandleAsync(Text("a lighthouse"), default);
        Assert.StartsWith(BotMessages.Text("PackageLimitReached", false), s.Bot.LastText);
        Assert.Contains("@loomi_sales", s.Bot.LastText);
    }

    [Fact]
    public async Task An_account_made_by_hand_hears_what_it_always_did()
    {
        using var s = await SetupAsync();
        await SetAsync(s.Factory, GrowthSettings.SiteContact, "@loomi_sales");
        await WithDbAsync(s.Factory, async db =>
        {
            var sara = new AppUser { Username = "sara", NormalizedUsername = "sara" };
            db.Users.Add(sara); db.TelegramLinks.Add(new TelegramLink { UserId = sara.Id, ChatId = Chat });
            return await db.SaveChangesAsync();
        });
        await s.Worker.HandleAsync(Text("a lighthouse"), default);
        Assert.Equal(BotMessages.Text("InsufficientCredits", false), s.Bot.LastText);
    }
}
