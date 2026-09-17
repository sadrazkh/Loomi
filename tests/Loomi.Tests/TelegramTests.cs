using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
/// <summary>Stands in for the Bot API. Nothing here opens a socket, and every call the worker makes is kept so a test can read what the user would see.</summary>
public class FakeTelegram : ITelegramClient
{
    public List<(long Chat, string Text)> Texts { get; } = [];
    public List<(long Chat, byte[] Photo, string Name, string? Caption)> Photos { get; } = [];
    /// <summary>File id to bytes. An id nobody put here is a file the Bot API will not hand over.</summary>
    public Dictionary<string, byte[]> Files { get; } = [];
    public string LastText => Texts[^1].Text;
    public Task<IReadOnlyList<Update>> GetUpdatesAsync(int offset, CancellationToken ct) => Task.FromResult<IReadOnlyList<Update>>([]);
    public Task SendTextAsync(long chat, string text, CancellationToken ct) { Texts.Add((chat, text)); return Task.CompletedTask; }
    public Task SendPhotoAsync(long chat, byte[] photo, string name, string? caption, CancellationToken ct) { Photos.Add((chat, photo, name, caption)); return Task.CompletedTask; }
    public Task<byte[]?> DownloadAsync(string fileId, long maxBytes, CancellationToken ct) =>
        Task.FromResult(Files.TryGetValue(fileId, out var bytes) && bytes.Length <= maxBytes ? bytes : null);
}
public class TelegramTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nS8AAAAASUVORK5CYII=");
    private const long Chat = 4242, Other = 777;

    private static TelegramBotWorker WorkerFor(AppFactory factory, FakeTelegram bot) => new(
        factory.Services.GetRequiredService<IServiceScopeFactory>(), bot, factory.Services.GetRequiredService<GenerationEvents>(),
        Options.Create(new TelegramOptions { BotToken = "test-token", MediaGroupMs = 1500 }),
        Options.Create(new BrowserOptions()), NullLogger<TelegramBotWorker>.Instance);
    private static Message Text(string text, long chat = Chat, string? language = null) =>
        new() { Chat = new() { Id = chat }, From = new() { Id = 1, FirstName = "u", LanguageCode = language }, Text = text };
    private static Message Photo(string fileId, string? caption = null, string? group = null, long chat = Chat) =>
        new() { Chat = new() { Id = chat }, From = new() { Id = 1, FirstName = "u" }, Caption = caption, MediaGroupId = group, Photo = [new() { FileId = fileId, Width = 1, Height = 1 }] };
    private static async Task<T> WithDbAsync<T>(AppFactory factory, Func<AppDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
    private static async Task<AppUser> MemberAsync(AppFactory factory, string username, int credits = 1000)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new AppUser { Username = username, NormalizedUsername = username };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<PasswordService>().Hash(user, username + "-strong-pass");
        db.Users.Add(user);
        if (credits != 0) db.CreditEntries.Add(new CreditEntry { UserId = user.Id, Amount = credits, Kind = CreditKind.Grant });
        await db.SaveChangesAsync();
        return user;
    }
    private static Task<TelegramLink> LinkAsync(AppFactory factory, Guid user, long chat = Chat) => WithDbAsync(factory, async db =>
    {
        var link = new TelegramLink { UserId = user, ChatId = chat };
        db.TelegramLinks.Add(link); await db.SaveChangesAsync(); return link;
    });
    private static async Task<HttpClient> SignInAsync(AppFactory factory, string username, string password)
    {
        var client = factory.CreateClient();
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/session/login", new { username, password })).StatusCode);
        session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        return client;
    }

    [Fact]
    public async Task A_valid_code_links_the_chat_and_an_expired_or_spent_one_does_not()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram(); var worker = WorkerFor(factory, bot);
        var sara = await MemberAsync(factory, "sara");
        var code = await WithDbAsync(factory, db => Linking.IssueAsync(db, sara.Id, DateTime.UtcNow, default));
        await worker.HandleAsync(Text("/start " + code.Code), default);
        Assert.Contains("sara", bot.LastText);
        Assert.Equal(sara.Id, await WithDbAsync(factory, db => db.TelegramLinks.Where(l => l.ChatId == Chat).Select(l => l.UserId).SingleAsync()));
        // The code is spent: quoting it from a second chat cannot link that one too.
        await worker.HandleAsync(Text("/start " + code.Code, chat: Other), default);
        Assert.Equal(1, await WithDbAsync(factory, db => db.TelegramLinks.CountAsync()));
        var omid = await MemberAsync(factory, "omid");
        var stale = await WithDbAsync(factory, db => Linking.IssueAsync(db, omid.Id, DateTime.UtcNow.AddMinutes(-30), default));
        await worker.HandleAsync(Text("/start " + stale.Code, chat: Other), default);
        Assert.Equal(1, await WithDbAsync(factory, db => db.TelegramLinks.CountAsync()));
        Assert.Equal(0, await WithDbAsync(factory, db => db.LinkCodes.CountAsync()));
    }

    [Fact]
    public async Task An_unlinked_chat_reaches_nothing()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram(); var worker = WorkerFor(factory, bot);
        bot.Files["f1"] = Png;
        foreach (var message in new[] { Text("a cat in a hat"), Text("/balance"), Text("/cancel"), Photo("f1", "make it blue") })
            await worker.HandleAsync(message, default);
        Assert.Equal(4, bot.Texts.Count);
        Assert.All(bot.Texts, t => Assert.Contains("not linked", t.Text));
        Assert.Equal(0, await WithDbAsync(factory, db => db.Generations.CountAsync()));
        Assert.Equal(0, await WithDbAsync(factory, db => db.Uploads.CountAsync()));
        Assert.Equal(0, await WithDbAsync(factory, db => db.Projects.CountAsync()));
    }

    [Fact]
    public async Task A_plain_message_becomes_one_generation_in_the_bots_own_project()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram(); var worker = WorkerFor(factory, bot);
        var sara = await MemberAsync(factory, "sara");
        await LinkAsync(factory, sara.Id);
        await worker.HandleAsync(Text("a lighthouse at dusk"), default);
        var g = await WithDbAsync(factory, db => db.Generations.AsNoTracking().Include(x => x.Project).SingleAsync());
        Assert.Equal("a lighthouse at dusk", g.Prompt);
        Assert.Equal(sara.Id, g.UserId);
        Assert.Equal(TelegramBotWorker.BotProject, g.Project.Title);
        Assert.Equal(RunStatus.Queued, g.Status);
        // Charged the same as the web: one entry point means one set of rules.
        Assert.Equal(990, await WithDbAsync(factory, db => db.BalanceAsync(sara.Id, default)));
        await worker.HandleAsync(Text("another one"), default);
        Assert.Equal(1, await WithDbAsync(factory, db => db.Projects.CountAsync()));
    }

    [Fact]
    public async Task An_album_becomes_one_generation_carrying_every_photo()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram(); var worker = WorkerFor(factory, bot);
        var sara = await MemberAsync(factory, "sara");
        await LinkAsync(factory, sara.Id);
        bot.Files["a"] = Png; bot.Files["b"] = Png; bot.Files["c"] = Png;
        await worker.HandleAsync(Photo("a", "use these three", "album-1"), default);
        await worker.HandleAsync(Photo("b", group: "album-1"), default);
        await worker.HandleAsync(Photo("c", group: "album-1"), default);
        // Nothing is submitted while photos are still arriving: the album has no marker for its last message.
        await worker.FlushAlbumsAsync(DateTime.UtcNow, default);
        Assert.Equal(0, await WithDbAsync(factory, db => db.Generations.CountAsync()));
        await worker.FlushAlbumsAsync(DateTime.UtcNow.AddSeconds(5), default);
        var g = await WithDbAsync(factory, db => db.Generations.AsNoTracking().Include(x => x.Inputs).SingleAsync());
        Assert.Equal("use these three", g.Prompt);
        Assert.Equal(3, g.Inputs.Count);
        Assert.Equal([0, 1, 2], g.Inputs.OrderBy(i => i.Order).Select(i => i.Order));
        Assert.All(g.Inputs, i => Assert.NotNull(i.UploadId));
        Assert.Equal(3, await WithDbAsync(factory, db => db.Uploads.CountAsync(u => u.UserId == sara.Id)));
    }

    [Fact]
    public async Task A_photo_that_is_not_an_image_is_refused_before_anything_is_queued()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram(); var worker = WorkerFor(factory, bot);
        await LinkAsync(factory, (await MemberAsync(factory, "sara")).Id);
        bot.Files["bad"] = Encoding.ASCII.GetBytes("<html>not an image at all</html>");
        await worker.HandleAsync(Photo("bad", "make it blue"), default);
        Assert.Contains("PNG", bot.LastText);
        Assert.Equal(0, await WithDbAsync(factory, db => db.Generations.CountAsync()));
        Assert.Equal(0, await WithDbAsync(factory, db => db.Uploads.CountAsync()));
        // A file the Bot API will not hand over is the same refusal, not a crash.
        await worker.HandleAsync(Photo("missing", "make it blue"), default);
        Assert.Equal(0, await WithDbAsync(factory, db => db.Uploads.CountAsync()));
        await worker.HandleAsync(Photo("bad"), default);
        Assert.Contains("caption", bot.LastText);
    }

    [Fact]
    public async Task A_refusal_from_the_queue_is_told_in_the_users_own_language()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram(); var worker = WorkerFor(factory, bot);
        var sara = await MemberAsync(factory, "sara", credits: 5);
        await LinkAsync(factory, sara.Id);
        await worker.HandleAsync(Text("a lighthouse", language: "fa-IR"), default);
        Assert.Equal(0, await WithDbAsync(factory, db => db.Generations.CountAsync()));
        Assert.Equal(BotMessages.Text("InsufficientCredits", true), bot.LastText);
        Assert.NotEqual(BotMessages.Text("InsufficientCredits", false), bot.LastText);
    }

    [Fact]
    public async Task Cancel_and_balance_only_ever_see_the_callers_own_work()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram(); var worker = WorkerFor(factory, bot);
        var sara = await MemberAsync(factory, "sara");
        var omid = await MemberAsync(factory, "omid");
        await LinkAsync(factory, sara.Id);
        await worker.HandleAsync(Text("mine one"), default);
        await worker.HandleAsync(Text("mine two"), default);
        await WithDbAsync(factory, async db =>
        {
            var project = new ImageProject { UserId = omid.Id, Title = "omid's" };
            db.Projects.Add(project);
            db.Generations.Add(new Generation { ProjectId = project.Id, UserId = omid.Id, Prompt = "not mine" });
            return await db.SaveChangesAsync();
        });
        await worker.HandleAsync(Text("/balance"), default);
        Assert.Contains("980", bot.LastText);
        await worker.HandleAsync(Text("/status"), default);
        Assert.Contains("2", bot.LastText);
        await worker.HandleAsync(Text("/cancel"), default);
        Assert.Equal(2, await WithDbAsync(factory, db => db.Generations.CountAsync(g => g.Status == RunStatus.Cancelled)));
        Assert.Equal(RunStatus.Queued, await WithDbAsync(factory, db => db.Generations.Where(g => g.UserId == omid.Id).Select(g => g.Status).SingleAsync()));
        // Cancelling gave the credits back, so the balance is whole again.
        Assert.Equal(1000, await WithDbAsync(factory, db => db.BalanceAsync(sara.Id, default)));
        await worker.HandleAsync(Text("/cancel"), default);
        Assert.Equal(BotMessages.Text("nothingToCancel", false), bot.LastText);
    }

    [Fact]
    public async Task A_finished_generation_is_answered_only_when_the_bot_asked_for_it()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram(); var worker = WorkerFor(factory, bot);
        var sara = await MemberAsync(factory, "sara");
        await LinkAsync(factory, sara.Id);
        await worker.HandleAsync(Text("a lighthouse at dusk"), default);
        var mine = await WithDbAsync(factory, db => db.Generations.AsNoTracking().SingleAsync());
        var web = await WithDbAsync(factory, async db =>
        {
            var project = new ImageProject { UserId = sara.Id, Title = "On the web" };
            var g = new Generation { ProjectId = project.Id, UserId = sara.Id, Prompt = "from the browser", Status = RunStatus.Completed };
            g.LocalImagePath = await factory.Services.GetRequiredService<IImageStorage>().SaveAsync(project.Id, g.Id, Png, default);
            db.Projects.Add(project); db.Generations.Add(g); await db.SaveChangesAsync(); return g;
        });
        await worker.DeliverAsync(GenerationEvent.From(web), default);
        Assert.Empty(bot.Photos);
        var path = await WithDbAsync(factory, async db =>
        {
            var g = await db.Generations.SingleAsync(x => x.Id == mine.Id);
            g.Status = RunStatus.Completed;
            g.LocalImagePath = await factory.Services.GetRequiredService<IImageStorage>().SaveAsync(g.ProjectId, g.Id, Png, default);
            await db.SaveChangesAsync(); return g.LocalImagePath;
        });
        await worker.DeliverAsync(GenerationEvent.From(new Generation { Id = mine.Id, ProjectId = mine.ProjectId, UserId = sara.Id, Status = RunStatus.Completed, LocalImagePath = path, Prompt = mine.Prompt }), default);
        var sent = Assert.Single(bot.Photos);
        Assert.Equal(Chat, sent.Chat);
        Assert.Equal(Png, sent.Photo);
        Assert.Equal("a lighthouse at dusk", sent.Caption);
        // A failure is told, not silently dropped.
        await worker.DeliverAsync(GenerationEvent.From(new Generation { Id = mine.Id, ProjectId = mine.ProjectId, UserId = sara.Id, Status = RunStatus.Failed, ErrorMessage = "NoImageReturned", Prompt = mine.Prompt }), default);
        Assert.Equal(BotMessages.Text("NoImageReturned", false), bot.LastText);
    }

    [Fact]
    public async Task The_link_endpoints_belong_to_a_session_and_to_one_account_each()
    {
        using var factory = new AppFactory(); var bot = new FakeTelegram(); var worker = WorkerFor(factory, bot);
        await MemberAsync(factory, "sara");
        using var client = await SignInAsync(factory, "sara", "sara-strong-pass");
        // Without a bot token configured there is nothing to link to, and the app says so rather than issuing a code nobody can use.
        var refused = await client.PostAsync("/api/telegram/link", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("TelegramNotConfigured", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        var status = await client.GetFromJsonAsync<JsonElement>("/api/telegram/link");
        Assert.False(status.GetProperty("linked").GetBoolean());
        Assert.False(status.GetProperty("configured").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/telegram/link")).StatusCode);
    }

    [Fact]
    public async Task A_configured_workspace_issues_a_code_links_it_and_unlinks_it()
    {
        using var factory = new TelegramFactory(); var bot = new FakeTelegram();
        var worker = new TelegramBotWorker(factory.Services.GetRequiredService<IServiceScopeFactory>(), bot, factory.Services.GetRequiredService<GenerationEvents>(),
            factory.Services.GetRequiredService<IOptions<TelegramOptions>>(), Options.Create(new BrowserOptions()), NullLogger<TelegramBotWorker>.Instance);
        await MemberAsync(factory, "sara");
        using var client = await SignInAsync(factory, "sara", "sara-strong-pass");
        var issued = await client.PostAsync("/api/telegram/link", null);
        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        var code = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        Assert.Equal(8, code.Length);
        // Reloading shows the same code rather than replacing the one being typed.
        Assert.Equal(code, (await client.GetFromJsonAsync<JsonElement>("/api/telegram/link")).GetProperty("code").GetString());
        await worker.HandleAsync(Text("/start " + code.ToLowerInvariant()), default);
        var linked = await client.GetFromJsonAsync<JsonElement>("/api/telegram/link");
        Assert.True(linked.GetProperty("linked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, linked.GetProperty("code").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/telegram/link", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/telegram/link")).StatusCode);
        Assert.Equal(0, await WithDbAsync(factory, db => db.TelegramLinks.CountAsync()));
    }
}
/// <summary>A workspace with a bot token, so the link endpoints behave as they would in a deployment that runs one.</summary>
public class TelegramFactory : AppFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Telegram:BotToken", "test-token");
        builder.UseSetting("Telegram:BotUsername", "loomi_test_bot");
    }
}
