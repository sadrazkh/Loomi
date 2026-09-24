using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Telegram;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Xunit;

namespace Loomi.Tests;
/// <summary>The owner gives the workspace its bot from Settings. Before, the only way was the server's configuration, so there was nowhere in the app to
/// connect Telegram at all.</summary>
public class BotSetupTests
{
    private sealed class BotFactory : AppFactory
    {
        public FakeTelegram Telegram { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => { services.RemoveAll<ITelegramClient>(); services.AddSingleton<ITelegramClient>(Telegram); });
        }
    }
    private static async Task<HttpClient> SignInAsync(AppFactory factory, string username, string password)
    {
        var client = factory.CreateClient();
        async Task<string> Csrf() => (await client.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("csrfToken").GetString()!;
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await Csrf());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/session/login", new { username, password })).StatusCode);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await Csrf());
        return client;
    }
    private static async Task<JsonElement> StatusAsync(HttpClient client) => await client.GetFromJsonAsync<JsonElement>("/api/telegram/link");

    [Fact]
    public async Task The_owner_sets_the_bot_in_the_app_and_linking_opens_at_once()
    {
        using var factory = new BotFactory();
        using var owner = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        Assert.False((await StatusAsync(owner)).GetProperty("configured").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync("/api/telegram/link", null)).StatusCode);

        var saved = await owner.PutAsJsonAsync("/api/telegram/bot", new { token = " " + FakeTelegram.GoodToken + " " });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(FakeTelegram.BotName, (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("botUsername").GetString());

        var status = await StatusAsync(owner);
        Assert.True(status.GetProperty("configured").GetBoolean());
        Assert.Equal(("app", FakeTelegram.BotName, "••••oken"), (status.GetProperty("botSource").GetString(), status.GetProperty("botUsername").GetString(), status.GetProperty("tokenHint").GetString()));
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync("/api/telegram/link", null)).StatusCode);
        // Sealed at rest: the database holds nothing that is the token.
        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Settings.AsNoTracking().FirstAsync(s => s.Key == BotSettings.TokenKey);
        Assert.DoesNotContain(FakeTelegram.GoodToken, stored.Value);
    }

    [Fact]
    public async Task A_token_Telegram_does_not_recognise_is_refused_and_nothing_is_kept()
    {
        using var factory = new BotFactory();
        using var owner = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        var refused = await owner.PutAsJsonAsync("/api/telegram/bot", new { token = "123456:mistyped" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("InvalidBotToken", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.False((await StatusAsync(owner)).GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task A_member_can_neither_set_the_bot_nor_see_where_it_comes_from()
    {
        using var factory = new BotFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var member = new AppUser { Username = "tester", NormalizedUsername = "tester", Role = UserRole.Member };
            member.PasswordHash = new PasswordService().Hash(member, "tester-password-1");
            db.Users.Add(member); await db.SaveChangesAsync();
        }
        using var owner = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/telegram/bot", new { token = FakeTelegram.GoodToken })).StatusCode);
        using var tester = await SignInAsync(factory, "tester", "tester-password-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await tester.PutAsJsonAsync("/api/telegram/bot", new { token = FakeTelegram.GoodToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tester.DeleteAsync("/api/telegram/bot")).StatusCode);
        var status = await StatusAsync(tester);
        Assert.True(status.GetProperty("configured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("tokenHint").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("botSource").ValueKind);
    }

    [Fact]
    public async Task Removing_the_bot_turns_linking_off_again()
    {
        using var factory = new BotFactory();
        using var owner = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        await owner.PutAsJsonAsync("/api/telegram/bot", new { token = FakeTelegram.GoodToken });
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync("/api/telegram/bot")).StatusCode);
        Assert.False((await StatusAsync(owner)).GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task A_saved_token_survives_a_restart()
    {
        using var factory = new BotFactory();
        using var owner = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        await owner.PutAsJsonAsync("/api/telegram/bot", new { token = FakeTelegram.GoodToken });
        // A fresh settings object reading the same database, as the next start would.
        var reloaded = new BotSettings(factory.Services.GetRequiredService<IServiceScopeFactory>(), factory.Services.GetRequiredService<AccountSecrets>(), Options.Create(new TelegramOptions()));
        Assert.False(reloaded.Configured);
        await reloaded.LoadAsync(default);
        Assert.Equal((FakeTelegram.GoodToken, FakeTelegram.BotName, "app"), (reloaded.Token, reloaded.Username, reloaded.Source));
    }

    [Fact]
    public async Task The_bot_starts_polling_the_moment_a_token_is_set_with_no_restart()
    {
        using var factory = new AppFactory();
        var telegram = new PollCounter();
        var bot = new BotSettings(factory.Services.GetRequiredService<IServiceScopeFactory>(), factory.Services.GetRequiredService<AccountSecrets>(), Options.Create(new TelegramOptions()));
        var worker = new TelegramBotWorker(factory.Services.GetRequiredService<IServiceScopeFactory>(), telegram, factory.Services.GetRequiredService<Loomi.Services.GenerationEvents>(),
            Options.Create(new TelegramOptions()), Options.Create(new Loomi.BrowserAutomation.BrowserOptions()), bot, Microsoft.Extensions.Logging.Abstractions.NullLogger<TelegramBotWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            await Task.Delay(300);
            Assert.Equal(0, telegram.Polls);
            await bot.SetAsync(FakeTelegram.GoodToken, FakeTelegram.BotName, default);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (telegram.Polls == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.True(telegram.Polls > 0);
        }
        finally { await worker.StopAsync(default); }
    }
    /// <summary>Counts long polls, and holds each briefly the way Telegram does, so the loop does not spin.</summary>
    private sealed class PollCounter : ITelegramClient
    {
        private int polls;
        public int Polls => Volatile.Read(ref polls);
        public async Task<IReadOnlyList<Update>> GetUpdatesAsync(int offset, CancellationToken ct) { Interlocked.Increment(ref polls); await Task.Delay(100, ct); return []; }
        public Task SendTextAsync(long chat, string text, CancellationToken ct) => Task.CompletedTask;
        public Task SendPhotoAsync(long chat, byte[] photo, string name, string? caption, CancellationToken ct) => Task.CompletedTask;
        public Task<byte[]?> DownloadAsync(string fileId, long maxBytes, CancellationToken ct) => Task.FromResult<byte[]?>(null);
        public Task<string?> WhoAmIAsync(string token, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
