using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.DTOs;
using Loomi.Models;
using Loomi.Providers;
using Loomi.Security;
using Loomi.Services;
using Loomi.Telegram;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;
using File = System.IO.File;

namespace Loomi.Tests;
/// <summary>A stand-in for ChatGPT that keeps every request it was given, so a test can read which files went in and which digests came along.</summary>
internal sealed class RecordingProvider(byte[] picture) : IImageProvider
{
    public ConcurrentQueue<GenerationRequest> Requests { get; } = new();
    public Provider Provider => Provider.ChatGPT;
    public bool IsBusy(ProviderAccount account) => false;
    public string StateOf(ProviderAccount account) => "Connected";
    public Task<string?> DesktopUrlAsync() => Task.FromResult<string?>(null);
    public Task<ConnectionStatus> StatusAsync(ProviderAccount account) => Task.FromResult(new ConnectionStatus("Connected", false, null));
    public Task<ConnectionStatus> ConnectAsync(ProviderAccount account, CancellationToken ct) => StatusAsync(account);
    public Task ResetAsync(ProviderAccount account, CancellationToken ct) => Task.CompletedTask;
    public Task DiscardAsync(Guid accountId) => Task.CompletedTask;
    public Task<BrowserResult> RunAsync(ProviderAccount account, GenerationRequest request, ReportStatus report, CancellationToken ct)
    {
        Requests.Enqueue(request);
        return Task.FromResult(new BrowserResult(picture, "https://chatgpt.com/c/one"));
    }
}
/// <summary>The real dispatcher, over a provider that needs no browser.</summary>
internal sealed class RecordingFactory(IImageProvider provider) : AppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Browser:ConnectOnStart", "false");
        builder.ConfigureServices(services => { services.RemoveAll<IImageProvider>(); services.AddSingleton(provider); services.AddHostedService<GenerationWorker>(); });
    }
}
/// <summary>What a gift pays for carries a small mark wherever it is handed out; what was paid for never does. The file on disk stays as ChatGPT made it,
/// because that file is compared with what ChatGPT returns and sent back to it.</summary>
public class WatermarkTests
{
    private const long Chat = 7100, OwnerChat = 7200;
    private static readonly SKColor Sky = new(122, 165, 207);
    private static byte[] Solid(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
    /// <summary>The smallest box around every pixel that moved from the picture's own flat colour.</summary>
    private static (int Left, int Top, int Right, int Bottom) Marked(SKBitmap picture, SKColor flat, int tolerance = 6)
    {
        var (left, top, right, bottom) = (picture.Width, picture.Height, -1, -1);
        for (var y = 0; y < picture.Height; y++)
            for (var x = 0; x < picture.Width; x++)
            {
                var c = picture.GetPixel(x, y);
                if (Math.Abs(c.Red - flat.Red) <= tolerance && Math.Abs(c.Green - flat.Green) <= tolerance && Math.Abs(c.Blue - flat.Blue) <= tolerance) continue;
                (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
            }
        return (left, top, right, bottom);
    }
    private static bool HasMark(byte[] bytes, SKColor flat)
    {
        using var picture = SKBitmap.Decode(bytes);
        return Marked(picture, flat).Right >= 0;
    }
    private static async Task<T> WithDbAsync<T>(AppFactory factory, Func<AppDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
    private static async Task<(Guid Project, Guid Generation, string Stored)> StoredAsync(AppFactory factory, byte[] bytes)
    {
        var (project, generation) = (Guid.NewGuid(), Guid.NewGuid());
        return (project, generation, await factory.Services.GetRequiredService<IImageStorage>().SaveAsync(project, generation, bytes, default));
    }
    private static string Folder(AppFactory factory, string stored) => Path.GetDirectoryName(factory.Services.GetRequiredService<IImageStorage>().Resolve(stored))!;
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    // ---- The mark itself ----

    [Fact]
    public void The_mark_is_embedded_in_the_assembly_and_has_a_clear_background()
    {
        using var stream = typeof(Watermark).Assembly.GetManifestResourceStream(Watermark.Resource);
        Assert.NotNull(stream);
        using var mark = SKBitmap.Decode(stream);
        Assert.True(mark.Width > 300 && mark.Width > mark.Height);
        Assert.Equal(0, mark.GetPixel(0, 0).Alpha);
        Assert.Equal(0, mark.GetPixel(mark.Width - 1, mark.Height - 1).Alpha);
    }

    [Fact]
    public async Task The_mark_sits_small_in_the_bottom_corner_and_the_stored_picture_is_left_alone()
    {
        using var factory = new AppFactory();
        var original = Solid(1024, 768, Sky);
        var (_, _, stored) = await StoredAsync(factory, original);
        var storage = factory.Services.GetRequiredService<IImageStorage>();

        var marked = await factory.Services.GetRequiredService<Watermark>().MarkedAsync(stored, default);
        Assert.Equal(stored[..(stored.LastIndexOf('/') + 1)] + "marked.png", marked);
        Assert.Equal(original, await File.ReadAllBytesAsync(storage.Resolve(stored)));

        var bytes = await File.ReadAllBytesAsync(storage.Resolve(marked!));
        Assert.Equal("png", ImageStorage.ExtensionFor(bytes));
        using var picture = SKBitmap.Decode(bytes);
        Assert.Equal((1024, 768), (picture.Width, picture.Height));
        var (left, top, right, bottom) = Marked(picture, Sky);
        var share = (right - left + 1) / 1024d;
        Assert.InRange(share, 0.16, 0.19);
        Assert.InRange(right, 1024 * 0.95, 1023);
        Assert.InRange(bottom, 768 * 0.95, 767);
        Assert.True(left > 1024 * 0.75 && top > 768 * 0.85, $"the mark reaches ({left},{top})");
    }

    [Fact]
    public async Task A_copy_is_made_once()
    {
        using var factory = new AppFactory();
        var (_, _, stored) = await StoredAsync(factory, Solid(300, 200, Sky));
        var marks = factory.Services.GetRequiredService<Watermark>();
        var storage = factory.Services.GetRequiredService<IImageStorage>();
        var first = storage.Resolve((await marks.MarkedAsync(stored, default))!);
        var made = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(first, made);
        await marks.MarkedAsync(stored, default);
        Assert.Equal(made, File.GetLastWriteTimeUtc(first));
        Assert.Empty(Directory.GetFiles(Folder(factory, stored), "*.tmp"));
    }

    [Theory]
    [InlineData(16, 18, 22)]
    [InlineData(250, 250, 248)]
    [InlineData(128, 128, 128)]
    public async Task The_mark_reads_on_dark_on_light_and_between(byte r, byte g, byte b)
    {
        using var factory = new AppFactory();
        var flat = new SKColor(r, g, b);
        var (_, _, stored) = await StoredAsync(factory, Solid(1024, 1024, flat));
        var marked = await factory.Services.GetRequiredService<Watermark>().MarkedAsync(stored, default);
        using var picture = SKBitmap.Decode(factory.Services.GetRequiredService<IImageStorage>().Resolve(marked!));
        var (lightest, darkest) = (0, 255);
        for (var y = 800; y < 1024; y++)
            for (var x = 700; x < 1024; x++)
            {
                var c = picture.GetPixel(x, y);
                (lightest, darkest) = (Math.Max(lightest, c.Red), Math.Min(darkest, c.Red));
            }
        // The white at about 70%, and an edge dark enough to draw its outline; together there is always something to see.
        Assert.True(lightest - darkest >= 70, $"on {flat} the mark spans only {darkest}..{lightest}");
        if (r < 40) Assert.InRange(lightest, 150, 205);
        if (r > 200) Assert.True(darkest < 190, $"on white the edge only reaches {darkest}");
    }

    [Fact]
    public async Task A_tiny_picture_comes_out_whole()
    {
        using var factory = new AppFactory();
        var (_, _, stored) = await StoredAsync(factory, Solid(1, 1, Sky));
        var marked = await factory.Services.GetRequiredService<Watermark>().MarkedAsync(stored, default);
        using var picture = SKBitmap.Decode(factory.Services.GetRequiredService<IImageStorage>().Resolve(marked!));
        Assert.Equal((1, 1), (picture.Width, picture.Height));
    }

    // ---- Who pays decides ----

    private sealed record Setup(AppFactory Factory, FakeTelegram Bot, TelegramBotWorker Worker, Guid User, Guid Owner, HttpClient Token, byte[] Original) : IDisposable
    {
        public void Dispose() { Token.Dispose(); Factory.Dispose(); }
    }
    private static Message Text(string text, long chat = Chat) => new() { Chat = new() { Id = chat, Type = ChatType.Private }, From = new() { Id = chat, FirstName = "u" }, Text = text };
    private static CallbackQuery Press(string data, long chat = Chat) =>
        new() { Id = Guid.NewGuid().ToString("N"), Data = data, From = new() { Id = chat, FirstName = "u" }, Message = new() { Chat = new() { Id = chat, Type = ChatType.Private } } };
    /// <summary>Someone signed up in the bot with one gift image, some regular credits and a token of their own, and the owner's chat linked.</summary>
    private static async Task<Setup> SetupAsync(AppFactory? factory = null, int credits = 100, byte[]? original = null)
    {
        factory ??= new AppFactory();
        var bot = new FakeTelegram();
        var worker = new TelegramBotWorker(factory.Services.GetRequiredService<IServiceScopeFactory>(), bot, factory.Services.GetRequiredService<GenerationEvents>(),
            Options.Create(new TelegramOptions { BotToken = "test-token", MediaGroupMs = 1500 }), Options.Create(new BrowserOptions()), TelegramTests.ConfiguredBot(factory),
            factory.Services.GetRequiredService<QueueEstimates>(), NullLogger<TelegramBotWorker>.Instance);
        using (var scope = factory.Services.CreateScope())
        {
            var growth = scope.ServiceProvider.GetRequiredService<GrowthSettings>();
            await growth.SetOnAsync(GrowthSettings.SignupOpen, true, default);
            await growth.SetAsync(GrowthSettings.SignupGiftImages, "1", default);
        }
        await worker.HandleCallbackAsync(Press("signup"), default);
        var name = Signup.UsernameFor(Chat);
        var (user, owner) = await WithDbAsync(factory, async db =>
        {
            var u = await db.Users.SingleAsync(x => x.NormalizedUsername == name);
            var o = await db.Users.SingleAsync(x => x.NormalizedUsername == OwnerBootstrap.Username);
            if (credits > 0) db.CreditEntries.Add(new CreditEntry { UserId = u.Id, Amount = credits, Kind = CreditKind.Grant });
            db.TelegramLinks.Add(new TelegramLink { UserId = o.Id, ChatId = OwnerChat });
            await db.SaveChangesAsync();
            return (u.Id, o.Id);
        });
        var (token, prefix, hash) = ApiTokens.Mint();
        await WithDbAsync(factory, async db => { db.ApiTokens.Add(new ApiToken { UserId = user, Name = "t", Prefix = prefix, Hash = hash }); return await db.SaveChangesAsync(); });
        var api = factory.CreateClient(new() { HandleCookies = false });
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new(factory, bot, worker, user, owner, api, original ?? Solid(800, 600, Sky));
    }
    /// <summary>A message in the chat, finished the way the worker finishes one, and answered.</summary>
    private static async Task<Generation> MakeAsync(Setup s, string prompt)
    {
        await s.Worker.HandleAsync(Text("/new"), default);
        await s.Worker.HandleAsync(Text(prompt), default);
        var done = await WithDbAsync(s.Factory, async db =>
        {
            var g = await db.Generations.OrderByDescending(x => x.CreatedAt).FirstAsync(x => x.Prompt == prompt);
            g.LocalImagePath = await s.Factory.Services.GetRequiredService<IImageStorage>().SaveAsync(g.ProjectId, g.Id, s.Original, default);
            g.Status = RunStatus.Completed; await db.SaveChangesAsync(); return g;
        });
        await s.Worker.DeliverAsync(GenerationEvent.From(done), default);
        return done;
    }

    [Fact]
    public async Task Gift_images_pay_before_regular_credits_and_then_the_credits_do()
    {
        using var s = await SetupAsync();
        var gift = await WithDbAsync(s.Factory, db => db.CreditPackages.AsNoTracking().SingleAsync());
        var first = await MakeAsync(s, "first");
        Assert.Equal(gift.Id, first.PackageId);
        Assert.Equal(100, await WithDbAsync(s.Factory, db => db.BalanceAsync(s.User, default)));
        var second = await MakeAsync(s, "second");
        Assert.Null(second.PackageId);
        Assert.Equal(90, await WithDbAsync(s.Factory, db => db.BalanceAsync(s.User, default)));
    }

    [Fact]
    public async Task A_gift_picture_is_marked_wherever_it_is_handed_out()
    {
        using var s = await SetupAsync();
        var g = await MakeAsync(s, "a gift");
        var storage = s.Factory.Services.GetRequiredService<IImageStorage>();
        var marked = await File.ReadAllBytesAsync(Path.Combine(Folder(s.Factory, g.LocalImagePath!), "marked.png"));
        Assert.True(HasMark(marked, Sky));
        // The original is what it was.
        Assert.Equal(s.Original, await File.ReadAllBytesAsync(storage.Resolve(g.LocalImagePath!)));

        // The bot's delivery, and its original-file button.
        Assert.Equal(marked, s.Bot.Photos[^1].Photo);
        Assert.EndsWith(".png", s.Bot.Photos[^1].Name);
        await s.Worker.HandleCallbackAsync(Press($"file:{g.Id:N}"), default);
        Assert.Equal(marked, s.Bot.Documents[^1].File);

        // The web's address and the API's.
        Assert.Equal(marked, await s.Token.GetByteArrayAsync($"/api/generations/{g.Id}/image"));
        Assert.Equal(marked, await s.Token.GetByteArrayAsync($"/api/v1/messages/{g.Id}/image"));

        // The owner's look at someone's latest pictures.
        await s.Worker.HandleCallbackAsync(Press($"uimg:{s.User:N}", OwnerChat), default);
        Assert.Equal(OwnerChat, s.Bot.Photos[^1].Chat);
        Assert.Equal(marked, s.Bot.Photos[^1].Photo);
        using var owner = await UserAdminTests.OwnerSessionAsync(s.Factory);
        Assert.Equal(marked, await owner.GetByteArrayAsync($"/api/generations/{g.Id}/image"));
    }

    [Fact]
    public async Task A_preview_of_a_gift_picture_is_made_from_the_marked_copy()
    {
        using var s = await SetupAsync();
        var g = await MakeAsync(s, "a gift");
        var folder = Folder(s.Factory, g.LocalImagePath!);
        var response = await s.Token.GetAsync($"/api/generations/{g.Id}/image?w=256");
        Assert.Equal((HttpStatusCode.OK, "image/webp"), (response.StatusCode, response.Content.Headers.ContentType?.MediaType));
        var preview = await response.Content.ReadAsByteArrayAsync();
        Assert.True(HasMark(preview, Sky), "the small copy has no mark on it");
        // Under a name of its own, so a copy of the unmarked picture could never stand in for it.
        Assert.True(File.Exists(Path.Combine(folder, "marked-preview-256.webp")));
        Assert.False(File.Exists(Path.Combine(folder, "preview-256.webp")));
    }

    [Fact]
    public async Task A_picture_paid_for_with_credits_is_marked_nowhere()
    {
        using var s = await SetupAsync();
        await MakeAsync(s, "the gift");
        var g = await MakeAsync(s, "paid for");
        Assert.Null(g.PackageId);
        Assert.Equal(s.Original, s.Bot.Photos[^1].Photo);
        await s.Worker.HandleCallbackAsync(Press($"file:{g.Id:N}"), default);
        Assert.Equal(s.Original, s.Bot.Documents[^1].File);
        Assert.Equal(s.Original, await s.Token.GetByteArrayAsync($"/api/generations/{g.Id}/image"));
        Assert.Equal(s.Original, await s.Token.GetByteArrayAsync($"/api/v1/messages/{g.Id}/image"));
        Assert.False(HasMark(await s.Token.GetByteArrayAsync($"/api/generations/{g.Id}/image?w=256"), Sky));
        await s.Worker.HandleCallbackAsync(Press($"uimg:{s.User:N}", OwnerChat), default);
        Assert.Equal(s.Original, s.Bot.Photos[^1].Photo);
        Assert.False(File.Exists(Path.Combine(Folder(s.Factory, g.LocalImagePath!), "marked.png")));
    }

    [Fact]
    public async Task A_package_the_owner_sold_is_not_a_gift_however_it_pays()
    {
        using var s = await SetupAsync();
        await MakeAsync(s, "the gift");
        var now = DateTime.UtcNow;
        await WithDbAsync(s.Factory, async db =>
        {
            db.CreditPackages.Add(new CreditPackage { UserId = s.User, Kind = PackageKind.Credits, Name = "sold", Amount = 100, StartsAt = now.AddMinutes(-1), EndsAt = now.AddDays(10) });
            return await db.SaveChangesAsync();
        });
        var g = await MakeAsync(s, "from the package");
        Assert.NotNull(g.PackageId);
        Assert.Equal(s.Original, s.Bot.Photos[^1].Photo);
    }

    [Fact]
    public async Task A_picture_that_cannot_be_marked_goes_out_as_it_is()
    {
        // The PNG signature passes the storage's check; nothing after it is a picture.
        byte[] broken = [137, 80, 78, 71, 13, 10, 26, 10, .. "not really a picture at all"u8];
        using var s = await SetupAsync(original: broken);
        var g = await MakeAsync(s, "a gift");
        Assert.Equal(broken, s.Bot.Photos[^1].Photo);
        Assert.Equal(broken, await s.Token.GetByteArrayAsync($"/api/generations/{g.Id}/image"));
        Assert.Equal(broken, await s.Token.GetByteArrayAsync($"/api/generations/{g.Id}/image?w=256"));
        Assert.Equal(["image.png"], Directory.GetFiles(Folder(s.Factory, g.LocalImagePath!)).Select(Path.GetFileName));
    }

    [Fact]
    public async Task The_marked_copy_goes_when_the_chat_does()
    {
        using var s = await SetupAsync();
        var g = await MakeAsync(s, "a gift");
        var folder = Folder(s.Factory, g.LocalImagePath!);
        Assert.True(Directory.Exists(folder));
        Assert.Equal(HttpStatusCode.NoContent, (await s.Token.DeleteAsync($"/api/v1/chats/{g.ProjectId}")).StatusCode);
        Assert.False(Directory.Exists(folder));
    }

    // ---- ChatGPT only ever sees the original ----

    /// <summary>A member with a gift, one picture finished by the real dispatcher, and its marked copy made, as a first delivery would.</summary>
    private static async Task<(RecordingFactory Factory, RecordingProvider Provider, AppUser User, Generation First, byte[] Marked)> FinishedGiftAsync(byte[] picture)
    {
        var provider = new RecordingProvider(picture);
        var factory = new RecordingFactory(provider);
        var user = await WithDbAsync(factory, async db =>
        {
            var now = DateTime.UtcNow;
            var member = new AppUser { Username = "sara", NormalizedUsername = "sara", FromBot = true };
            db.Users.Add(member);
            db.CreditPackages.Add(new CreditPackage { UserId = member.Id, Kind = PackageKind.Credits, IsGift = true, Name = "gift", Amount = 100, StartsAt = now.AddMinutes(-1), EndsAt = now.AddDays(30) });
            db.Accounts.Add(new ProviderAccount { Label = "GPT1", ProfileDirectory = "gpt1" });
            await db.SaveChangesAsync();
            return member;
        });
        Generation first;
        using (var scope = factory.Services.CreateScope())
            first = await scope.ServiceProvider.GetRequiredService<GenerationService>().StartAsync(Viewer.For(user), null, "a lighthouse", null, null, default);
        first = await SettledAsync(factory, first.Id);
        Assert.NotNull(first.PackageId);
        var stored = await WithDbAsync(factory, db => factory.Services.GetRequiredService<Watermark>().DeliveredAsync(db, first.PackageId, first.LocalImagePath!, default));
        Assert.EndsWith("marked.png", stored);
        return (factory, provider, user, first, await File.ReadAllBytesAsync(factory.Services.GetRequiredService<IImageStorage>().Resolve(stored)));
    }
    private static async Task<Generation> SettledAsync(AppFactory factory, Guid id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var g = await WithDbAsync(factory, db => db.Generations.AsNoTracking().FirstAsync(x => x.Id == id));
            if (g.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Answered) return g;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Generation {id} never finished.");
    }

    [Fact]
    public async Task Digests_of_the_conversation_are_of_the_original_so_the_old_picture_is_still_recognised()
    {
        var picture = Solid(400, 300, Sky);
        var (factory, provider, user, first, marked) = await FinishedGiftAsync(picture);
        await using (factory)
        {
            Assert.NotEqual(picture, marked);
            using (var scope = factory.Services.CreateScope())
            {
                var next = await scope.ServiceProvider.GetRequiredService<GenerationService>().SendAsync(Viewer.For(user), first.ProjectId, "and now at night", null, null, default);
                await SettledAsync(factory, next.Id);
            }
            var continued = provider.Requests.Last();
            Assert.Equal(first.ConversationUrl, continued.ParentConversation);
            Assert.Contains(Sha(picture), continued.KnownImages!);
            Assert.DoesNotContain(Sha(marked), continued.KnownImages!);
        }
    }

    [Fact]
    public async Task What_goes_back_to_ChatGPT_is_the_original()
    {
        var picture = Solid(400, 300, Sky);
        var (factory, provider, user, first, _) = await FinishedGiftAsync(picture);
        await using (factory)
        {
            var original = factory.Services.GetRequiredService<IImageStorage>().Resolve(first.LocalImagePath!);
            using (var scope = factory.Services.CreateScope())
            {
                var service = scope.ServiceProvider.GetRequiredService<GenerationService>();
                // Once as the parent of a new direction and once named as a reference: the two ways a finished picture goes back in.
                var branch = await service.SubmitAsync(Viewer.For(user), first.ProjectId, "another direction", Provider.ChatGPT, Operation.Branch, first.Id, null, default);
                await SettledAsync(factory, branch.Id);
                Assert.Equal([original], provider.Requests.Last().InputPaths);
                var reference = await service.StartAsync(Viewer.For(user), null, "in this style", [new InputRef(null, first.Id)], null, default);
                await SettledAsync(factory, reference.Id);
                Assert.Equal([original], provider.Requests.Last().InputPaths);
            }
        }
    }
}
