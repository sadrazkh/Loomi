using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Xunit;
using File = System.IO.File;

namespace Loomi.Tests;
/// <summary>The news the bot sends on its own: new templates, an invitation back and a word about low credits. Nothing goes out until the owner turns a
/// kind on, nobody hears anything twice, and anyone can make it stop.</summary>
public class NotificationTests
{
    private const long SaraChat = 22, OmidChat = 33, OwnerChat = 11;
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nS8AAAAASUVORK5CYII=");
    /// <summary>A moment on the server's clock, whatever zone the tests run in, counted in days from a fixed noon.</summary>
    private static DateTime Day(int days, int hour = 12, int minute = 0) => new DateTime(2030, 6, 12, hour, minute, 0, DateTimeKind.Local).AddDays(days).ToUniversalTime();
    /// <summary>A Bot API that fails for the chats it is told to, and otherwise behaves like <see cref="FakeTelegram"/>.</summary>
    private sealed class Flaky(FakeTelegram inner, Func<long, Exception?> fails) : ITelegramClient
    {
        public Task<IReadOnlyList<Update>> GetUpdatesAsync(int offset, CancellationToken ct) => inner.GetUpdatesAsync(offset, ct);
        public Task<int> SendTextAsync(long chat, string text, CancellationToken ct, IReadOnlyList<IReadOnlyList<BotButton>>? buttons = null) =>
            fails(chat) is { } e ? throw e : inner.SendTextAsync(chat, text, ct, buttons);
        public Task SendPhotoAsync(long chat, byte[] photo, string name, string? caption, CancellationToken ct, IReadOnlyList<IReadOnlyList<BotButton>>? buttons = null) =>
            fails(chat) is { } e ? throw e : inner.SendPhotoAsync(chat, photo, name, caption, ct, buttons);
        public Task SendDocumentAsync(long chat, byte[] file, string name, string? caption, CancellationToken ct) => inner.SendDocumentAsync(chat, file, name, caption, ct);
        public Task EditTextAsync(long chat, int message, string text, CancellationToken ct) => inner.EditTextAsync(chat, message, text, ct);
        public Task DeleteAsync(long chat, int message, CancellationToken ct) => inner.DeleteAsync(chat, message, ct);
        public Task AnswerAsync(string callback, CancellationToken ct) => inner.AnswerAsync(callback, ct);
        public Task SetCommandsAsync(IReadOnlyList<BotCommandText> commands, CancellationToken ct) => inner.SetCommandsAsync(commands, ct);
        public Task<byte[]?> DownloadAsync(string fileId, long maxBytes, CancellationToken ct) => inner.DownloadAsync(fileId, maxBytes, ct);
        public Task<string?> WhoAmIAsync(string token, CancellationToken ct) => inner.WhoAmIAsync(token, ct);
    }
    /// <summary>A workspace with a bot, for what reads the bot's state over HTTP.</summary>
    private sealed class BotFactory : AppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Telegram:BotToken", "test-token");
            builder.ConfigureServices(services => { services.RemoveAll<ITelegramClient>(); services.AddSingleton<ITelegramClient>(new FakeTelegram()); });
        }
    }
    private static NotificationWorker News(AppFactory factory, ITelegramClient bot) =>
        new(factory.Services.GetRequiredService<IServiceScopeFactory>(), bot, TelegramTests.ConfiguredBot(factory), NullLogger<NotificationWorker>.Instance);
    private static TelegramBotWorker Bot(AppFactory factory, FakeTelegram bot) => new(factory.Services.GetRequiredService<IServiceScopeFactory>(), bot,
        factory.Services.GetRequiredService<GenerationEvents>(), Options.Create(new TelegramOptions { BotToken = "test-token", MediaGroupMs = 1500 }),
        Options.Create(new BrowserOptions()), TelegramTests.ConfiguredBot(factory), factory.Services.GetRequiredService<QueueEstimates>(), NullLogger<TelegramBotWorker>.Instance);
    private static Message Text(string text, long chat, string? language = null) => new() { Chat = new() { Id = chat }, From = new() { Id = 1, FirstName = "u", LanguageCode = language }, Text = text };
    private static CallbackQuery Press(string data, long chat, string? language = null) =>
        new() { Id = Guid.NewGuid().ToString("N"), Data = data, From = new() { Id = 1, FirstName = "u", LanguageCode = language }, Message = new() { Chat = new() { Id = chat } } };
    private static async Task<T> WithDbAsync<T>(AppFactory factory, Func<AppDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
    private static async Task TurnOnAsync(AppFactory factory, params string[] keys)
    {
        using var scope = factory.Services.CreateScope();
        var growth = scope.ServiceProvider.GetRequiredService<GrowthSettings>();
        foreach (var key in keys) await growth.SetOnAsync(key, true, default);
    }
    private static async Task SetAsync(AppFactory factory, string key, string value)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<GrowthSettings>().SetAsync(key, value, default);
    }
    private static async Task<string?> SettingAsync(AppFactory factory, string key)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<GrowthSettings>().GetAsync(key, default);
    }
    /// <summary>A linked member with this many credits, and when they last asked for something; null has them never ask.</summary>
    private static Task<Guid> MemberAsync(AppFactory factory, string name, long chat, int credits = 1000, DateTime? asked = null, bool disabled = false, bool off = false) => WithDbAsync(factory, async db =>
    {
        var user = new AppUser { Username = name, NormalizedUsername = name, Role = UserRole.Member, IsDisabled = disabled };
        db.Users.Add(user);
        db.CreditEntries.Add(new CreditEntry { UserId = user.Id, Amount = credits, Kind = CreditKind.Grant });
        db.TelegramLinks.Add(new TelegramLink { UserId = user.Id, ChatId = chat, NotificationsOff = off });
        await db.SaveChangesAsync();
        if (asked is { } at) await AskedAsync(factory, user.Id, at);
        return user.Id;
    });
    private static Task AskedAsync(AppFactory factory, Guid user, DateTime at) => WithDbAsync(factory, async db =>
    {
        var project = new ImageProject { UserId = user, Title = "p" };
        db.Projects.Add(project);
        db.Generations.Add(new Generation { ProjectId = project.Id, UserId = user, Prompt = "x", Status = RunStatus.Completed, CreatedAt = at });
        return await db.SaveChangesAsync();
    });
    private static Task CreditAsync(AppFactory factory, Guid user, int amount) => WithDbAsync(factory, async db =>
    {
        db.CreditEntries.Add(new CreditEntry { UserId = user, Amount = amount, Kind = amount > 0 ? CreditKind.Grant : CreditKind.Charge });
        return await db.SaveChangesAsync();
    });
    private static Task PackageAsync(AppFactory factory, Guid user, PackageKind kind, DateTime starts, DateTime ends, int amount = 100) => WithDbAsync(factory, async db =>
    {
        db.CreditPackages.Add(new CreditPackage { UserId = user, Kind = kind, Amount = amount, Name = "p", StartsAt = starts, EndsAt = ends });
        return await db.SaveChangesAsync();
    });
    private static async Task<Guid> TemplateAsync(AppFactory factory, string title, DateTime created, bool active = true, bool sample = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var category = new TemplateCategory { Name = "c" };
        var template = new PromptTemplate { CategoryId = category.Id, Title = title, Prompt = "HIDDEN", CreatedAt = created, IsActive = active };
        if (sample) template.SamplePath = await scope.ServiceProvider.GetRequiredService<IImageStorage>().SaveTemplateAsync(template.Id, Png, default);
        db.TemplateCategories.Add(category); db.Templates.Add(template); await db.SaveChangesAsync();
        return template.Id;
    }
    private static Task<PromptTemplate> TemplateRowAsync(AppFactory factory, Guid id) => WithDbAsync(factory, db => db.Templates.AsNoTracking().SingleAsync(t => t.Id == id));
    private static Task<TelegramLink> LinkAsync(AppFactory factory, long chat) => WithDbAsync(factory, db => db.TelegramLinks.AsNoTracking().SingleAsync(l => l.ChatId == chat));
    private static string SamplePath(AppFactory factory, PromptTemplate template) => factory.Services.GetRequiredService<IImageStorage>().Resolve(template.SamplePath!);

    [Fact]
    public async Task With_every_switch_off_nothing_is_sent_and_nothing_is_stamped()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        await MemberAsync(factory, "sara", SaraChat, credits: 5, asked: Day(-20));
        var template = await TemplateAsync(factory, "Golden hour", Day(-1));

        await News(factory, bot).RunAsync(Day(0), default);

        Assert.Empty(bot.Texts);
        Assert.Empty(bot.Photos);
        var link = await LinkAsync(factory, SaraChat);
        Assert.Null(link.LastNudgeAt);
        Assert.Null(link.LowCreditNudgeAt);
        Assert.Null((await TemplateRowAsync(factory, template)).AnnouncedAt);
        Assert.Null(await SettingAsync(factory, GrowthSettings.NotifyDigestAt));
    }

    [Fact]
    public async Task Without_a_bot_nothing_is_sent_or_stamped_even_with_the_switches_on()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        await MemberAsync(factory, "sara", SaraChat);
        var template = await TemplateAsync(factory, "Golden hour", Day(-1));
        await TurnOnAsync(factory, GrowthSettings.NotifyNewTemplates);
        var none = new BotSettings(factory.Services.GetRequiredService<IServiceScopeFactory>(), factory.Services.GetRequiredService<AccountSecrets>(), Options.Create(new TelegramOptions()));

        await new NotificationWorker(factory.Services.GetRequiredService<IServiceScopeFactory>(), bot, none, NullLogger<NotificationWorker>.Instance).RunAsync(Day(0), default);

        Assert.Empty(bot.Photos);
        Assert.Null((await TemplateRowAsync(factory, template)).AnnouncedAt);
    }

    [Fact]
    public async Task The_digest_is_one_photo_per_chat_of_the_newest_sample_and_stamps_what_it_covered()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        await MemberAsync(factory, "sara", SaraChat);
        await MemberAsync(factory, "omid", OmidChat);
        var older = await TemplateAsync(factory, "Old look", Day(-3));
        var newest = await TemplateAsync(factory, "Golden hour", Day(-1));
        var inactive = await TemplateAsync(factory, "Hidden look", Day(0, 1), active: false);
        var noSample = await TemplateAsync(factory, "Unfinished look", Day(0, 2), sample: false);
        await TurnOnAsync(factory, GrowthSettings.NotifyNewTemplates);
        var worker = News(factory, bot);

        await worker.RunAsync(Day(0), default);

        Assert.Empty(bot.Texts);
        Assert.Equal([SaraChat, OmidChat], bot.Photos.Select(p => p.Chat).Order());
        var (_, photo, name, caption) = bot.Photos[0];
        Assert.Equal(Png, photo);
        Assert.EndsWith(".png", name);
        Assert.Contains("قالب تازه: «Golden hour» و ۱ قالب دیگر", caption);
        Assert.Contains("New template: “Golden hour”, and 1 more", caption);
        Assert.DoesNotContain("Hidden look", caption);
        Assert.DoesNotContain("Unfinished look", caption);
        var buttons = bot.PhotoButtons[0]!.SelectMany(row => row).ToList();
        Assert.Equal([$"tpl:{newest:N}", "notifyoff"], buttons.Select(b => b.Data));
        Assert.Contains("امتحانش کن", buttons[0].Text);
        Assert.Contains("خاموش کردن اعلان‌ها", buttons[1].Text);
        Assert.NotNull((await TemplateRowAsync(factory, newest)).AnnouncedAt);
        Assert.NotNull((await TemplateRowAsync(factory, older)).AnnouncedAt);
        Assert.Null((await TemplateRowAsync(factory, inactive)).AnnouncedAt);
        Assert.Null((await TemplateRowAsync(factory, noSample)).AnnouncedAt);
        Assert.NotNull(await SettingAsync(factory, GrowthSettings.NotifyDigestAt));

        // The next look, and a week later with nothing new, add nothing.
        await worker.RunAsync(Day(0, 12, 15), default);
        await worker.RunAsync(Day(8), default);
        Assert.Equal(2, bot.Photos.Count);
    }

    [Fact]
    public async Task A_fresh_template_waits_for_the_next_window_and_a_late_sample_counts_as_fresh()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        await MemberAsync(factory, "sara", SaraChat);
        await TemplateAsync(factory, "First look", Day(-1));
        await TurnOnAsync(factory, GrowthSettings.NotifyNewTemplates);
        var worker = News(factory, bot);
        await worker.RunAsync(Day(0), default);
        Assert.Single(bot.Photos);

        var fresh = await TemplateAsync(factory, "Second look", Day(3));
        var unfinished = await TemplateAsync(factory, "Third look", Day(3), sample: false);
        await worker.RunAsync(Day(3), default);
        await worker.RunAsync(Day(6, 19, 59), default);
        Assert.Single(bot.Photos);
        Assert.Null((await TemplateRowAsync(factory, fresh)).AnnouncedAt);

        await worker.RunAsync(Day(7), default);
        Assert.Equal(2, bot.Photos.Count);
        var caption = bot.Photos[^1].Caption!;
        Assert.Contains("«Second look»", caption);
        Assert.DoesNotContain("First look", caption);
        Assert.DoesNotContain("more", caption);
        Assert.NotNull((await TemplateRowAsync(factory, fresh)).AnnouncedAt);

        // Having a sample only now, it is new news, and it waits its week like any other.
        await WithDbAsync(factory, async db =>
        {
            var row = await db.Templates.SingleAsync(t => t.Id == unfinished);
            row.SamplePath = await factory.Services.GetRequiredService<IImageStorage>().SaveTemplateAsync(row.Id, Png, default);
            return await db.SaveChangesAsync();
        });
        await worker.RunAsync(Day(8), default);
        Assert.Equal(2, bot.Photos.Count);
        await worker.RunAsync(Day(14), default);
        Assert.Contains("«Third look»", bot.Photos[^1].Caption);
    }

    [Fact]
    public async Task The_digest_says_nothing_of_a_sample_it_cannot_show_and_leaves_it_waiting()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        await MemberAsync(factory, "sara", SaraChat);
        var shown = await TemplateAsync(factory, "Shown look", Day(-3));
        var gone = await TemplateAsync(factory, "Gone look", Day(-2));
        var huge = await TemplateAsync(factory, "Huge look", Day(-1));
        File.Delete(SamplePath(factory, await TemplateRowAsync(factory, gone)));
        // Telegram takes a photo of at most ten megabytes; the owner may keep a sample twice that.
        await using (var stream = File.OpenWrite(SamplePath(factory, await TemplateRowAsync(factory, huge)))) stream.SetLength(10 * 1024 * 1024 + 1);
        await TurnOnAsync(factory, GrowthSettings.NotifyNewTemplates);

        await News(factory, bot).RunAsync(Day(0), default);

        var photo = Assert.Single(bot.Photos);
        Assert.Contains("«Shown look»", photo.Caption);
        Assert.DoesNotContain("more", photo.Caption);
        Assert.NotNull((await TemplateRowAsync(factory, shown)).AnnouncedAt);
        Assert.Null((await TemplateRowAsync(factory, gone)).AnnouncedAt);
        Assert.Null((await TemplateRowAsync(factory, huge)).AnnouncedAt);
    }

    [Fact]
    public async Task With_nothing_to_show_or_nobody_to_tell_the_digest_keeps_its_templates_and_its_week()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        var template = await TemplateAsync(factory, "Golden hour", Day(-1));
        await TurnOnAsync(factory, GrowthSettings.NotifyNewTemplates);
        await News(factory, bot).RunAsync(Day(0), default);
        Assert.Empty(bot.Photos);
        Assert.Null((await TemplateRowAsync(factory, template)).AnnouncedAt);
        Assert.Null(await SettingAsync(factory, GrowthSettings.NotifyDigestAt));

        // The first person to link hears about it at the next look, not a week on.
        await MemberAsync(factory, "sara", SaraChat);
        await News(factory, bot).RunAsync(Day(0, 12, 15), default);
        Assert.Single(bot.Photos);
    }

    [Fact]
    public async Task The_longest_titles_still_fit_in_a_caption()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        await MemberAsync(factory, "sara", SaraChat);
        await TemplateAsync(factory, new string('x', 80), Day(-2));
        await TemplateAsync(factory, new string('y', 80), Day(-1));
        await TurnOnAsync(factory, GrowthSettings.NotifyNewTemplates);
        await News(factory, bot).RunAsync(Day(0), default);
        Assert.True(Assert.Single(bot.Photos).Caption!.Length <= 1024);
    }

    [Fact]
    public async Task A_stop_part_way_never_sends_the_digest_to_anyone_again()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        await MemberAsync(factory, "sara", SaraChat);
        await MemberAsync(factory, "omid", OmidChat);
        var template = await TemplateAsync(factory, "Golden hour", Day(-1));
        await TurnOnAsync(factory, GrowthSettings.NotifyNewTemplates);
        using var stopping = new CancellationTokenSource();
        var sends = 0;
        var stoppedHalfWay = new Flaky(bot, _ =>
        {
            if (++sends < 2) return null;
            stopping.Cancel();
            return new OperationCanceledException(stopping.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => News(factory, stoppedHalfWay).RunAsync(Day(0), stopping.Token));
        Assert.Single(bot.Photos);
        Assert.NotNull((await TemplateRowAsync(factory, template)).AnnouncedAt);

        // The restart looks at once: the one already told is not told again, and the rest wait for the next digest.
        await News(factory, bot).RunAsync(Day(0, 12, 1), default);
        Assert.Single(bot.Photos);
    }

    [Fact]
    public async Task The_templates_are_stamped_before_the_first_message_goes()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        await MemberAsync(factory, "sara", SaraChat);
        var template = await TemplateAsync(factory, "Golden hour", Day(-1));
        await TurnOnAsync(factory, GrowthSettings.NotifyNewTemplates);
        DateTime? announcedAtSend = null; string? digestAtSend = null;
        var looking = new Flaky(bot, _ =>
        {
            announcedAtSend = TemplateRowAsync(factory, template).GetAwaiter().GetResult().AnnouncedAt;
            digestAtSend = SettingAsync(factory, GrowthSettings.NotifyDigestAt).GetAwaiter().GetResult();
            return null;
        });

        await News(factory, looking).RunAsync(Day(0), default);

        Assert.NotNull(announcedAtSend);
        Assert.NotNull(digestAtSend);
    }

    [Fact]
    public async Task A_digest_nobody_could_be_sent_is_as_if_it_never_went()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        await MemberAsync(factory, "sara", SaraChat);
        await MemberAsync(factory, "omid", OmidChat);
        var template = await TemplateAsync(factory, "Golden hour", Day(-1));
        await TurnOnAsync(factory, GrowthSettings.NotifyNewTemplates);

        await News(factory, new Flaky(bot, _ => new HttpRequestException("unreachable"))).RunAsync(Day(0), default);
        Assert.Empty(bot.Photos);
        Assert.Null((await TemplateRowAsync(factory, template)).AnnouncedAt);

        await News(factory, bot).RunAsync(Day(0, 12, 15), default);
        Assert.Equal(2, bot.Photos.Count);
        Assert.NotNull((await TemplateRowAsync(factory, template)).AnnouncedAt);
    }

    [Fact]
    public async Task A_digest_telegram_refuses_is_not_tried_again_every_quarter_hour()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        await MemberAsync(factory, "sara", SaraChat);
        var template = await TemplateAsync(factory, "Golden hour", Day(-1));
        await TurnOnAsync(factory, GrowthSettings.NotifyNewTemplates);

        await News(factory, new Flaky(bot, _ => new ApiRequestException("Bad Request: PHOTO_INVALID_DIMENSIONS", 400))).RunAsync(Day(0), default);
        Assert.NotNull((await TemplateRowAsync(factory, template)).AnnouncedAt);

        await News(factory, bot).RunAsync(Day(0, 12, 15), default);
        Assert.Empty(bot.Photos);
    }

    [Fact]
    public async Task News_goes_out_only_between_ten_and_twenty_on_the_servers_clock()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        var sara = await MemberAsync(factory, "sara", SaraChat, credits: 5, asked: Day(-20));
        var first = await TemplateAsync(factory, "First look", Day(-1));
        await TurnOnAsync(factory, GrowthSettings.NotifyNewTemplates, GrowthSettings.NotifyWinBack, GrowthSettings.NotifyLowCredit);
        var worker = News(factory, bot);

        foreach (var night in new[] { Day(0, 0), Day(0, 3), Day(0, 9, 59), Day(0, 20), Day(0, 23, 45) }) await worker.RunAsync(night, default);
        Assert.Empty(bot.Photos);
        Assert.Empty(bot.Texts);
        var link = await LinkAsync(factory, SaraChat);
        Assert.Null(link.LastNudgeAt);
        Assert.Null(link.LowCreditNudgeAt);
        Assert.Null((await TemplateRowAsync(factory, first)).AnnouncedAt);

        await worker.RunAsync(Day(1, 10), default);
        Assert.Single(bot.Photos);

        await TemplateAsync(factory, "Second look", Day(2));
        await worker.RunAsync(Day(8, 20), default);
        Assert.Single(bot.Photos);
        await worker.RunAsync(Day(8, 19, 59), default);
        Assert.Equal(2, bot.Photos.Count);
    }

    [Fact]
    public async Task Sending_is_paced_at_about_twenty_messages_a_second()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        for (var i = 0; i < 6; i++) await MemberAsync(factory, $"member{i}", 100 + i);
        await TemplateAsync(factory, "Golden hour", Day(-1));
        await TurnOnAsync(factory, GrowthSettings.NotifyNewTemplates);

        var clock = Stopwatch.StartNew();
        await News(factory, bot).RunAsync(Day(0), default);
        clock.Stop();

        Assert.Equal(6, bot.Photos.Count);
        Assert.True(clock.Elapsed >= 5 * NotificationWorker.Pace - TimeSpan.FromMilliseconds(20), $"six messages took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Whoever_turned_it_off_hears_nothing_and_a_block_turns_it_off_without_stopping_the_rest()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        await MemberAsync(factory, "sara", SaraChat);
        await MemberAsync(factory, "omid", OmidChat);
        await MemberAsync(factory, "nima", 44);
        await MemberAsync(factory, "quiet", 55, off: true);
        await TemplateAsync(factory, "First look", Day(-1));
        await TurnOnAsync(factory, GrowthSettings.NotifyNewTemplates);
        var failing = new Flaky(bot, chat => chat switch
        {
            SaraChat => new ApiRequestException("Forbidden: bot was blocked by the user", 403),
            OmidChat => new HttpRequestException("unreachable"),
            _ => null,
        });

        await News(factory, failing).RunAsync(Day(0), default);

        Assert.Equal(44, Assert.Single(bot.Photos).Chat);
        Assert.True((await LinkAsync(factory, SaraChat)).NotificationsOff);
        Assert.False((await LinkAsync(factory, OmidChat)).NotificationsOff);

        await TemplateAsync(factory, "Second look", Day(3));
        await News(factory, bot).RunAsync(Day(7), default);
        Assert.Equal([OmidChat, 44L, 44L], bot.Photos.Select(p => p.Chat).Order());
    }

    [Fact]
    public async Task Someone_who_turns_it_off_during_a_long_run_is_not_sent_the_rest_of_it()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        await MemberAsync(factory, "sara", SaraChat);
        await MemberAsync(factory, "omid", OmidChat);
        await TemplateAsync(factory, "Golden hour", Day(-1));
        await TurnOnAsync(factory, GrowthSettings.NotifyNewTemplates);
        // Whoever is first turns the other off, as a press arriving between two sends would.
        var pressed = new Flaky(bot, chat =>
        {
            var other = chat == SaraChat ? OmidChat : SaraChat;
            WithDbAsync(factory, db => db.TelegramLinks.Where(l => l.ChatId == other).ExecuteUpdateAsync(s => s.SetProperty(l => l.NotificationsOff, true))).GetAwaiter().GetResult();
            return null;
        });
        await News(factory, pressed).RunAsync(Day(0), default);
        Assert.Single(bot.Photos);
    }

    [Fact]
    public async Task Winning_someone_back_waits_fourteen_quiet_days_and_a_month_between_invitations()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        var sara = await MemberAsync(factory, "sara", SaraChat, asked: Day(-15));
        await MemberAsync(factory, "recent", OmidChat, asked: Day(-13));
        await MemberAsync(factory, "never", 44);
        await TurnOnAsync(factory, GrowthSettings.NotifyWinBack);
        var worker = News(factory, bot);

        await worker.RunAsync(Day(0), default);

        var (chat, text) = Assert.Single(bot.Texts);
        Assert.Equal(SaraChat, chat);
        Assert.Contains("مدتی است ندیدیمتان", text);
        Assert.Contains("It has been a while", text);
        Assert.Equal(["templates", "notifyoff"], bot.TextButtons[0]!.SelectMany(row => row).Select(b => b.Data));
        Assert.NotNull((await LinkAsync(factory, SaraChat)).LastNudgeAt);
        Assert.Null((await LinkAsync(factory, OmidChat)).LastNudgeAt);
        Assert.Null((await LinkAsync(factory, 44)).LastNudgeAt);

        await worker.RunAsync(Day(0, 12, 15), default);
        await worker.RunAsync(Day(29), default);
        Assert.Single(bot.Texts, t => t.Chat == SaraChat);

        await worker.RunAsync(Day(31), default);
        Assert.Equal(2, bot.Texts.Count(t => t.Chat == SaraChat));

        // Coming back resets the fourteen days, and only silence after that invites again.
        await AskedAsync(factory, sara, Day(62));
        await worker.RunAsync(Day(63), default);
        Assert.Equal(2, bot.Texts.Count(t => t.Chat == SaraChat));
        await worker.RunAsync(Day(77), default);
        Assert.Equal(3, bot.Texts.Count(t => t.Chat == SaraChat));
    }

    [Fact]
    public async Task An_invitation_that_could_not_be_sent_is_tried_again_and_one_telegram_refused_is_not()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        await MemberAsync(factory, "sara", SaraChat, asked: Day(-20));
        await MemberAsync(factory, "omid", OmidChat, asked: Day(-20));
        await TurnOnAsync(factory, GrowthSettings.NotifyWinBack);
        var failing = new Flaky(bot, chat => chat == SaraChat ? new HttpRequestException("unreachable") : new ApiRequestException("Bad Request: chat not found", 400));

        await News(factory, failing).RunAsync(Day(0), default);
        Assert.Null((await LinkAsync(factory, SaraChat)).LastNudgeAt);
        Assert.NotNull((await LinkAsync(factory, OmidChat)).LastNudgeAt);

        await News(factory, bot).RunAsync(Day(0, 12, 15), default);
        Assert.Equal(SaraChat, Assert.Single(bot.Texts).Chat);
    }

    [Fact]
    public async Task Each_invitation_is_stamped_before_it_goes_so_a_crash_cannot_repeat_it()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        await MemberAsync(factory, "sara", SaraChat, credits: 5, asked: Day(-20));
        await TurnOnAsync(factory, GrowthSettings.NotifyWinBack);
        DateTime? atSend = null;
        await News(factory, new Flaky(bot, chat => { atSend = LinkAsync(factory, chat).GetAwaiter().GetResult().LastNudgeAt; return null; })).RunAsync(Day(0), default);
        Assert.NotNull(atSend);
    }

    [Fact]
    public async Task Low_credit_is_said_once_until_a_top_up_and_again_at_the_next_dip()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        // The seeded ChatGPT price is ten, so the line is twenty.
        var sara = await MemberAsync(factory, "sara", SaraChat, credits: 15, asked: Day(-1));
        await MemberAsync(factory, "line", OmidChat, credits: 20, asked: Day(-1));
        await MemberAsync(factory, "new", 44, credits: 5);
        await TurnOnAsync(factory, GrowthSettings.NotifyLowCredit);
        var worker = News(factory, bot);

        await worker.RunAsync(Day(0), default);
        var (chat, text) = Assert.Single(bot.Texts);
        Assert.Equal(SaraChat, chat);
        Assert.Contains("۱۵ اعتبار مانده", text);
        Assert.Contains("15 left", text);
        Assert.Contains("notifyoff", bot.TextButtons[0]!.SelectMany(row => row).Select(b => b.Data));
        Assert.NotNull((await LinkAsync(factory, SaraChat)).LowCreditNudgeAt);

        await worker.RunAsync(Day(3), default);
        Assert.Single(bot.Texts);

        await CreditAsync(factory, sara, 100);
        await worker.RunAsync(Day(4), default);
        Assert.Null((await LinkAsync(factory, SaraChat)).LowCreditNudgeAt);
        Assert.Single(bot.Texts);

        await CreditAsync(factory, sara, -110);
        await worker.RunAsync(Day(5), default);
        Assert.Equal(2, bot.Texts.Count);
        Assert.Contains("۵ اعتبار مانده", bot.LastText);
    }

    [Fact]
    public async Task A_top_up_at_night_is_noticed_so_the_next_morning_dip_is_told()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        var sara = await MemberAsync(factory, "sara", SaraChat, credits: 15, asked: Day(-1));
        await TurnOnAsync(factory, GrowthSettings.NotifyLowCredit);
        var worker = News(factory, bot);
        await worker.RunAsync(Day(0), default);

        await CreditAsync(factory, sara, 100);
        await worker.RunAsync(Day(1, 23), default);
        Assert.Null((await LinkAsync(factory, SaraChat)).LowCreditNudgeAt);
        Assert.Single(bot.Texts);

        await CreditAsync(factory, sara, -100);
        await worker.RunAsync(Day(2, 10), default);
        Assert.Equal(2, bot.Texts.Count);
    }

    [Fact]
    public async Task Low_credit_counts_running_credits_packages_and_skips_anyone_with_a_running_allowance()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        var now = Day(0);
        var withCredits = await MemberAsync(factory, "credits", 41, credits: 5, asked: Day(-1));
        await PackageAsync(factory, withCredits, PackageKind.Credits, now.AddDays(-1), now.AddDays(1));
        var spent = await MemberAsync(factory, "spent", 42, credits: 5, asked: Day(-1));
        await PackageAsync(factory, spent, PackageKind.Credits, now.AddDays(-1), now.AddDays(1), amount: 10);
        var unlimited = await MemberAsync(factory, "unlimited", 43, credits: 0, asked: Day(-1));
        await PackageAsync(factory, unlimited, PackageKind.Unlimited, now.AddDays(-1), now.AddDays(1), amount: 0);
        var daily = await MemberAsync(factory, "daily", 44, credits: 0, asked: Day(-1));
        await PackageAsync(factory, daily, PackageKind.Daily, now.AddDays(-1), now.AddDays(1));
        var ended = await MemberAsync(factory, "ended", 45, credits: 0, asked: Day(-1));
        await PackageAsync(factory, ended, PackageKind.Daily, now.AddDays(-9), now.AddDays(-1));
        await PackageAsync(factory, ended, PackageKind.Credits, now.AddDays(-9), now.AddDays(-1));
        var waiting = await MemberAsync(factory, "waiting", 46, credits: 0, asked: Day(-1));
        await PackageAsync(factory, waiting, PackageKind.Unlimited, now.AddDays(1), now.AddDays(2), amount: 0);
        await TurnOnAsync(factory, GrowthSettings.NotifyLowCredit);

        await News(factory, bot).RunAsync(now, default);

        Assert.Equal([42L, 45L, 46L], bot.Texts.Select(t => t.Chat).Order());
    }

    [Fact]
    public async Task Low_credit_says_where_to_buy_only_when_the_owner_has_written_it()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        var sara = await MemberAsync(factory, "sara", SaraChat, credits: 5, asked: Day(-1));
        await TurnOnAsync(factory, GrowthSettings.NotifyLowCredit);
        var worker = News(factory, bot);

        await worker.RunAsync(Day(0), default);
        Assert.DoesNotContain("برای خرید", bot.LastText);
        Assert.DoesNotContain("To buy", bot.LastText);

        await CreditAsync(factory, sara, 100);
        await worker.RunAsync(Day(1), default);
        await CreditAsync(factory, sara, -100);
        await SetAsync(factory, GrowthSettings.SiteContact, "   ");
        await worker.RunAsync(Day(2), default);
        Assert.Equal(2, bot.Texts.Count);
        Assert.DoesNotContain("برای خرید", bot.LastText);

        await CreditAsync(factory, sara, 100);
        await worker.RunAsync(Day(3), default);
        await CreditAsync(factory, sara, -100);
        await SetAsync(factory, GrowthSettings.SiteContact, " @loomi_sales ");
        await worker.RunAsync(Day(4), default);
        Assert.Equal(3, bot.Texts.Count);
        Assert.Contains("برای خرید اعتبار: @loomi_sales", bot.LastText);
        Assert.Contains("To buy more: @loomi_sales", bot.LastText);
    }

    [Fact]
    public async Task With_no_price_nobody_is_ever_low()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        await MemberAsync(factory, "sara", SaraChat, credits: 0, asked: Day(-1));
        await WithDbAsync(factory, async db => { await db.PricingRules.ExecuteDeleteAsync(); return 0; });
        await TurnOnAsync(factory, GrowthSettings.NotifyLowCredit);
        await News(factory, bot).RunAsync(Day(0), default);
        Assert.Empty(bot.Texts);
        Assert.Null((await LinkAsync(factory, SaraChat)).LowCreditNudgeAt);
    }

    [Fact]
    public async Task Owners_and_disabled_members_and_people_without_a_chat_never_hear_any_of_it()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        await WithDbAsync(factory, async db =>
        {
            var owner = await db.Users.SingleAsync(u => u.NormalizedUsername == OwnerBootstrap.Username);
            db.TelegramLinks.Add(new TelegramLink { UserId = owner.Id, ChatId = OwnerChat });
            var project = new ImageProject { UserId = owner.Id, Title = "p" };
            db.Projects.Add(project);
            db.Generations.Add(new Generation { ProjectId = project.Id, UserId = owner.Id, Prompt = "x", Status = RunStatus.Completed, CreatedAt = Day(-20) });
            return await db.SaveChangesAsync();
        });
        await MemberAsync(factory, "gone", 55, credits: 0, asked: Day(-20), disabled: true);
        await WithDbAsync(factory, async db =>
        {
            db.Users.Add(new AppUser { Username = "nochat", NormalizedUsername = "nochat", Role = UserRole.Member });
            return await db.SaveChangesAsync();
        });
        await TemplateAsync(factory, "Golden hour", Day(-1));
        await TurnOnAsync(factory, GrowthSettings.NotifyNewTemplates, GrowthSettings.NotifyWinBack, GrowthSettings.NotifyLowCredit);
        await News(factory, bot).RunAsync(Day(0), default);
        await News(factory, bot).RunAsync(Day(1), default);
        Assert.Empty(bot.Photos);
        Assert.Empty(bot.Texts);
    }

    [Fact]
    public async Task One_person_hears_at_most_one_piece_of_news_a_day()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        await MemberAsync(factory, "sara", SaraChat, credits: 5, asked: Day(-20));
        await TemplateAsync(factory, "Golden hour", Day(-1));
        await TurnOnAsync(factory, GrowthSettings.NotifyNewTemplates, GrowthSettings.NotifyWinBack, GrowthSettings.NotifyLowCredit);
        var worker = News(factory, bot);

        await worker.RunAsync(Day(0), default);
        await worker.RunAsync(Day(0, 12, 15), default);
        await worker.RunAsync(Day(0, 19, 45), default);
        Assert.Single(bot.Photos);
        Assert.Empty(bot.Texts);

        await worker.RunAsync(Day(1), default);
        await worker.RunAsync(Day(1, 19, 45), default);
        Assert.Contains("اعتبار مانده", Assert.Single(bot.Texts).Text);

        await worker.RunAsync(Day(2), default);
        Assert.Equal(2, bot.Texts.Count);
        Assert.Contains("مدتی است ندیدیمتان", bot.LastText);

        await worker.RunAsync(Day(3), default);
        Assert.Equal(2, bot.Texts.Count);
    }

    [Fact]
    public async Task Slash_notify_toggles_and_says_which_way_it_now_stands()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram(); var worker = Bot(factory, bot);
        await MemberAsync(factory, "sara", SaraChat);

        await worker.HandleAsync(Text("/notify", SaraChat, "en"), default);
        Assert.True((await LinkAsync(factory, SaraChat)).NotificationsOff);
        Assert.Equal(BotMessages.Text("notifyNowOff", false), bot.LastText);
        Assert.Equal("notifyon", Assert.Single(bot.TextButtons[^1]!.SelectMany(row => row)).Data);

        await worker.HandleAsync(Text("/notify", SaraChat, "fa"), default);
        Assert.False((await LinkAsync(factory, SaraChat)).NotificationsOff);
        Assert.Equal(BotMessages.Text("notifyNowOn", true), bot.LastText);
        Assert.Equal("notifyoff", Assert.Single(bot.TextButtons[^1]!.SelectMany(row => row)).Data);
    }

    [Fact]
    public async Task The_off_button_turns_it_off_one_way_and_offers_it_back()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram(); var worker = Bot(factory, bot);
        await MemberAsync(factory, "sara", SaraChat);

        foreach (var _ in new[] { 1, 2 })
        {
            await worker.HandleCallbackAsync(Press("notifyoff", SaraChat, "fa"), default);
            Assert.True((await LinkAsync(factory, SaraChat)).NotificationsOff);
            Assert.Equal(BotMessages.Text("notifyNowOff", true), bot.LastText);
            Assert.Equal("notifyon", Assert.Single(bot.TextButtons[^1]!.SelectMany(row => row)).Data);
        }

        await worker.HandleCallbackAsync(Press("notifyon", SaraChat, "fa"), default);
        Assert.False((await LinkAsync(factory, SaraChat)).NotificationsOff);
        Assert.Equal(BotMessages.Text("notifyNowOn", true), bot.LastText);
    }

    [Fact]
    public async Task The_owner_is_told_the_news_is_not_for_them()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram(); var worker = Bot(factory, bot);
        await WithDbAsync(factory, async db =>
        {
            var owner = await db.Users.SingleAsync(u => u.NormalizedUsername == OwnerBootstrap.Username);
            db.TelegramLinks.Add(new TelegramLink { UserId = owner.Id, ChatId = OwnerChat });
            return await db.SaveChangesAsync();
        });
        await worker.HandleAsync(Text("/notify", OwnerChat, "en"), default);
        Assert.Equal(BotMessages.Text("notifyOwner", false), bot.LastText);
    }

    [Fact]
    public async Task An_unlinked_chat_cannot_press_its_way_in()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram(); var worker = Bot(factory, bot);
        await worker.HandleCallbackAsync(Press("notifyoff", 99), default);
        Assert.Equal(BotMessages.Text("linkFirst", false), bot.LastText);
    }

    [Fact]
    public async Task Turning_the_news_off_leaves_the_service_messages_coming()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        var sara = await MemberAsync(factory, "sara", SaraChat, off: true);
        var now = DateTime.UtcNow;
        await PackageAsync(factory, sara, PackageKind.Daily, now.AddDays(-1), now.AddDays(1));

        await new PackageReminderWorker(factory.Services.GetRequiredService<IServiceScopeFactory>(), bot, TelegramTests.ConfiguredBot(factory), NullLogger<PackageReminderWorker>.Instance).RunAsync(now, default);
        Assert.Equal(SaraChat, Assert.Single(bot.Texts).Chat);

        await Bot(factory, bot).HandleAsync(Text("/balance", SaraChat, "en"), default);
        Assert.StartsWith("Credits: 1000.", bot.LastText);
    }

    [Fact]
    public async Task The_try_it_button_opens_the_template_and_help_and_the_menu_name_the_command()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram(); var worker = Bot(factory, bot);
        await MemberAsync(factory, "sara", SaraChat);
        var template = await TemplateAsync(factory, "Golden hour", Day(-1));
        await TurnOnAsync(factory, GrowthSettings.NotifyNewTemplates);
        await News(factory, bot).RunAsync(Day(0), default);

        await worker.HandleCallbackAsync(Press(bot.PhotoButtons[0]![0][0].Data, SaraChat), default);
        Assert.Equal(2, bot.Photos.Count);
        Assert.Contains($"use:{template:N}", bot.PhotoButtons[^1]!.SelectMany(row => row).Select(b => b.Data));

        // English first: a chat that has once shown Persian stays Persian.
        foreach (var (language, persian) in new[] { ("en", false), ("fa", true) })
        {
            await worker.HandleAsync(Text("/help", SaraChat, language), default);
            Assert.Contains("/notify", bot.LastText);
            Assert.Equal(BotMessages.Help(persian), bot.LastText);
        }
        var command = Assert.Single(TelegramBotWorker.Commands, c => c.Command == "notify");
        Assert.True(BotMessages.LooksPersian(command.Fa) && !BotMessages.LooksPersian(command.En));
    }

    [Fact]
    public void The_persian_copy_reads_with_its_half_spaces_and_no_middle_dots()
    {
        foreach (var key in new[] { "notifyNewTemplate", "notifyNewTemplates", "notifyWinBack", "notifyLowCredit", "notifyBuyAt", "notifyBtnTry", "notifyBtnTemplates", "notifyBtnOff", "notifyBtnOn", "notifyNowOff", "notifyNowOn", "notifyOwner", "helpNotify", "cmdNotify" })
        {
            var persian = BotMessages.Text(key, true, "x", "y");
            var english = BotMessages.Text(key, false, "x", "y");
            Assert.NotEqual(BotMessages.Text("AutomationFailed", true), persian);
            Assert.True(BotMessages.LooksPersian(persian), key);
            Assert.False(BotMessages.LooksPersian(english), key);
            Assert.DoesNotContain("·", persian);
        }
        Assert.Contains("اعلان‌ها", BotMessages.Text("notifyBtnOff", true));
        Assert.Contains("قالب‌ها", BotMessages.Text("notifyBtnTemplates", true));
    }

    // ---- The owner's card ----

    private static async Task<HttpClient> MemberSessionAsync(AppFactory factory, string name, string password)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = new AppUser { Username = name, NormalizedUsername = name, Role = UserRole.Member };
            user.PasswordHash = scope.ServiceProvider.GetRequiredService<PasswordService>().Hash(user, password);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }
        var client = factory.CreateClient();
        async Task<string> CsrfAsync() => (await client.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("csrfToken").GetString()!;
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await CsrfAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/session/login", new { username = name, password })).StatusCode);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await CsrfAsync());
        return client;
    }

    [Fact]
    public async Task The_settings_are_the_owners_alone()
    {
        using var factory = new AppFactory();
        var member = await MemberSessionAsync(factory, "mia", "mia-password-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/notifications/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync("/api/notifications/settings", new { winBack = true })).StatusCode);
        Assert.Null(await SettingAsync(factory, GrowthSettings.NotifyWinBack));
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/notifications/settings")).StatusCode);
    }

    [Fact]
    public async Task The_owner_reads_the_switches_and_whom_each_would_reach_and_changes_one_at_a_time()
    {
        using var factory = new BotFactory();
        var now = DateTime.UtcNow;
        await MemberAsync(factory, "quiet", 41, asked: now.AddDays(-20));
        await MemberAsync(factory, "low", 42, credits: 5, asked: now.AddHours(-1));
        await MemberAsync(factory, "both", 43, credits: 5, asked: now.AddDays(-20));
        await MemberAsync(factory, "busy", 44, asked: now.AddHours(-1));
        await MemberAsync(factory, "off", 45, credits: 0, asked: now.AddDays(-20), off: true);
        await MemberAsync(factory, "gone", 46, credits: 0, asked: now.AddDays(-20), disabled: true);
        await TemplateAsync(factory, "Golden hour", now.AddDays(-1));
        var owner = await UserAdminTests.OwnerSessionAsync(factory);

        var read = await owner.GetFromJsonAsync<JsonElement>("/api/notifications/settings");
        Assert.True(read.GetProperty("telegram").GetBoolean());
        Assert.False(read.GetProperty("newTemplates").GetBoolean());
        Assert.False(read.GetProperty("winBack").GetBoolean());
        Assert.False(read.GetProperty("lowCredit").GetBoolean());
        var reach = read.GetProperty("reach");
        Assert.Equal(4, reach.GetProperty("newTemplates").GetInt32());
        Assert.Equal(2, reach.GetProperty("winBack").GetInt32());
        Assert.Equal(2, reach.GetProperty("lowCredit").GetInt32());
        Assert.Equal(1, reach.GetProperty("waitingTemplates").GetInt32());
        Assert.Equal(JsonValueKind.Null, reach.GetProperty("lastDigestAt").ValueKind);

        var changed = await owner.PutAsJsonAsync("/api/notifications/settings", new { winBack = true });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var after = await changed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(after.GetProperty("winBack").GetBoolean());
        Assert.False(after.GetProperty("newTemplates").GetBoolean());
        Assert.Equal("on", await SettingAsync(factory, GrowthSettings.NotifyWinBack));
        Assert.Null(await SettingAsync(factory, GrowthSettings.NotifyNewTemplates));

        changed = await owner.PutAsJsonAsync("/api/notifications/settings", new { lowCredit = true, winBack = false });
        after = await changed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(after.GetProperty("winBack").GetBoolean());
        Assert.True(after.GetProperty("lowCredit").GetBoolean());
    }

    [Fact]
    public async Task The_card_says_when_the_last_digest_went_and_when_there_is_no_bot()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram();
        await MemberAsync(factory, "sara", SaraChat);
        await TemplateAsync(factory, "Golden hour", Day(-1));
        await TurnOnAsync(factory, GrowthSettings.NotifyNewTemplates);
        await News(factory, bot).RunAsync(Day(0), default);
        var owner = await UserAdminTests.OwnerSessionAsync(factory);

        var read = await owner.GetFromJsonAsync<JsonElement>("/api/notifications/settings");

        Assert.False(read.GetProperty("telegram").GetBoolean());
        Assert.True(read.GetProperty("newTemplates").GetBoolean());
        var reach = read.GetProperty("reach");
        Assert.Equal(0, reach.GetProperty("waitingTemplates").GetInt32());
        Assert.Equal(Day(0), reach.GetProperty("lastDigestAt").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task A_token_cannot_turn_the_news_on()
    {
        using var factory = new AppFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = await db.Users.SingleAsync(u => u.NormalizedUsername == OwnerBootstrap.Username);
        var (token, prefix, hash) = ApiTokens.Mint();
        db.ApiTokens.Add(new ApiToken { UserId = owner.Id, Name = "script", Prefix = prefix, Hash = hash });
        await db.SaveChangesAsync();
        var client = factory.CreateClient(new() { HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/notifications/settings", new { winBack = true })).StatusCode);
        Assert.Null(await SettingAsync(factory, GrowthSettings.NotifyWinBack));
    }
}
