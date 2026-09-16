using System.Collections.Concurrent;
using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using Xunit;

namespace Loomi.Tests;
/// <summary>A ChatGPT stand-in per account: one can answer without ever making an image, and every run can be held mid-flight so "at the same time" is observed rather than timed.</summary>
public sealed class DispatchLauncher : IChromiumLauncher, IAsyncDisposable
{
    private readonly SemaphoreSlim launching = new(1, 1);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<IBrowserContext> contexts = [];
    private IPlaywright? playwright;
    private int held;
    /// <summary>Profile folders whose account has no image allowance left: it replies, but never with a picture.</summary>
    public HashSet<string> Imageless { get; init; } = [];
    /// <summary>Holds every run just after the prompt is sent, until <see cref="Release"/>.</summary>
    public bool Hold { get; init; }
    public ConcurrentQueue<string> Launched { get; } = new();
    public int Held => Volatile.Read(ref held);
    public void Release() => release.TrySetResult();
    public async Task<IBrowserContext> LaunchAsync(string profile, BrowserOptions options)
    {
        var account = Path.GetFileName(profile);
        await launching.WaitAsync();
        Launched.Enqueue(account);
        playwright ??= await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var context = await browser.NewContextAsync();
        contexts.Add(context);
        launching.Release();
        var body = Page(Imageless.Contains(account), Hold);
        await context.RouteAsync("**/*", async route =>
        {
            if (!route.Request.Url.EndsWith("/held")) { await route.FulfillAsync(new() { ContentType = "text/html", Body = body }); return; }
            Interlocked.Increment(ref held);
            await release.Task;
            await route.FulfillAsync(new() { ContentType = "text/plain", Body = "go" });
        });
        return context;
    }
    private static string Page(bool imageless, bool hold) => $$"""
        <html><body>
        <button data-testid="accounts-profile-button">Account</button>
        <textarea id="prompt-textarea"></textarea><input type="file" onchange="document.querySelector('#ready').hidden=false">
        <button id="ready" aria-label="Remove file" hidden>Remove</button>
        <button data-testid="send-button" onclick="send()">Send</button>
        <script>
        async function send() {
          history.replaceState({},'', '/c/' + Math.random().toString(36).slice(2));
          if ({{(hold ? "true" : "false")}}) await fetch('/held');
          const reply = document.createElement('div'); reply.dataset.messageAuthorRole = 'assistant';
          if ({{(imageless ? "true" : "false")}}) { reply.textContent = 'You have reached your image generation limit.'; document.body.append(reply); return; }
          const canvas = document.createElement('canvas'); canvas.width = canvas.height = 256;
          const ctx = canvas.getContext('2d'); ctx.fillStyle = '#7aa5cf'; ctx.fillRect(0,0,256,256);
          const image = document.createElement('img'); image.alt = 'Generated image'; image.src = canvas.toDataURL('image/png');
          reply.append(image); document.body.append(reply);
        }
        </script></body></html>
        """;
    public async ValueTask DisposeAsync() { Release(); foreach (var open in contexts) if (open.Browser is { } b) await b.CloseAsync(); playwright?.Dispose(); launching.Dispose(); }
}
/// <summary>The dispatcher the application itself registers, over browsers that never leave this machine.</summary>
public class DispatchFactory(IChromiumLauncher launcher) : AppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Browser:StableSeconds", "1");
        builder.UseSetting("Browser:GenerationTimeoutSeconds", "60");
        builder.ConfigureServices(services => { services.RemoveAll<IChromiumLauncher>(); services.AddSingleton(launcher); services.AddHostedService<GenerationWorker>(); });
    }
}
public class DispatcherTests
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nS8AAAAASUVORK5CYII=";
    private static async Task<Guid> AccountAsync(AppFactory factory, string profile, DateTime? lastUsed = null, int cap = 25)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = new ProviderAccount { Label = profile, ProfileDirectory = profile, DailyCap = cap, LastUsedAt = lastUsed };
        db.Accounts.Add(account); await db.SaveChangesAsync();
        return account.Id;
    }
    private static async Task<Guid> OwnerAsync(AppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking().FirstAsync(u => u.NormalizedUsername == OwnerBootstrap.Username)).Id;
    }
    private static async Task<Guid> QueueAsync(AppFactory factory, string prompt)
    {
        var owner = await OwnerAsync(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var project = new ImageProject { UserId = owner, Title = prompt };
        var generation = new Generation { ProjectId = project.Id, UserId = owner, Prompt = prompt };
        db.Projects.Add(project); db.Generations.Add(generation); await db.SaveChangesAsync();
        return generation.Id;
    }
    /// <summary>Work an account has already done today, which is what its cap is measured against.</summary>
    private static async Task SpendAsync(AppFactory factory, Guid account, int count)
    {
        var owner = await OwnerAsync(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var project = new ImageProject { UserId = owner, Title = "Earlier today" };
        db.Projects.Add(project);
        for (var i = 0; i < count; i++) db.Generations.Add(new Generation { ProjectId = project.Id, UserId = owner, AccountId = account, Prompt = $"Earlier {i}", Status = RunStatus.Completed });
        await db.SaveChangesAsync();
    }
    /// <summary>Polls rather than sleeping a guessed interval: the dispatcher is asynchronous by design.</summary>
    private static async Task<Generation> SettledAsync(AppFactory factory, Guid id, int seconds = 150)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var status = RunStatus.Queued;
        while (DateTime.UtcNow < deadline)
        {
            using (var scope = factory.Services.CreateScope())
            {
                var g = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Generations.AsNoTracking().FirstAsync(x => x.Id == id);
                if (g.Status is RunStatus.Completed or RunStatus.Failed) return g;
                status = g.Status;
            }
            await Task.Delay(200);
        }
        throw new TimeoutException($"Generation {id} was still {status} after {seconds}s.");
    }
    private static async Task UntilAsync(Func<bool> ready, string what, int seconds = 150)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline) { if (ready()) return; await Task.Delay(100); }
        throw new TimeoutException(what);
    }

    [Fact]
    public async Task Two_accounts_hold_two_generations_at_the_same_time()
    {
        await using var launcher = new DispatchLauncher { Hold = true };
        await using var factory = new DispatchFactory(launcher);
        await AccountAsync(factory, "first");
        await AccountAsync(factory, "second");
        var a = await QueueAsync(factory, "a quiet harbour at dawn");
        var b = await QueueAsync(factory, "the same harbour at dusk");
        await UntilAsync(() => launcher.Held == 2, "the two accounts never had a prompt in flight at the same time");
        launcher.Release();
        var first = await SettledAsync(factory, a);
        var second = await SettledAsync(factory, b);
        Assert.Equal(RunStatus.Completed, first.Status);
        Assert.Equal(RunStatus.Completed, second.Status);
        Assert.NotNull(first.AccountId);
        Assert.NotEqual(first.AccountId, second.AccountId);
    }

    [Fact]
    public async Task Two_dispatchers_racing_for_one_generation_leave_exactly_one_holder()
    {
        using var factory = new AppFactory();   // the claim is the thing under test, so nothing else may run the row
        var id = await QueueAsync(factory, "the contested one");
        var first = await AccountAsync(factory, "first");
        var second = await AccountAsync(factory, "second");
        using var a = factory.Services.CreateScope();
        using var b = factory.Services.CreateScope();
        var claims = await Task.WhenAll(
            GenerationWorker.ClaimAsync(a.ServiceProvider.GetRequiredService<AppDbContext>(), id, first, default),
            GenerationWorker.ClaimAsync(b.ServiceProvider.GetRequiredService<AppDbContext>(), id, second, default));
        Assert.Single(claims, won => won);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var claimed = await db.Generations.AsNoTracking().FirstAsync(x => x.Id == id);
        Assert.Equal(claims[0] ? first : second, claimed.AccountId);
        Assert.Equal(RunStatus.OpeningBrowser, claimed.Status);
        Assert.False(await GenerationWorker.ClaimAsync(db, id, await AccountAsync(factory, "third"), default));
    }

    [Fact]
    public async Task An_account_that_has_reached_its_daily_cap_is_passed_over()
    {
        await using var launcher = new DispatchLauncher();
        await using var factory = new DispatchFactory(launcher);
        var full = await AccountAsync(factory, "full", DateTime.UtcNow.AddHours(-2), cap: 1);
        var free = await AccountAsync(factory, "free", DateTime.UtcNow.AddHours(-1));
        await SpendAsync(factory, full, 1);
        var settled = await SettledAsync(factory, await QueueAsync(factory, "needs the other account"));
        Assert.Equal(RunStatus.Completed, settled.Status);
        Assert.Equal(free, settled.AccountId);
        Assert.DoesNotContain("full", launcher.Launched);
    }

    [Fact]
    public async Task An_account_that_answers_without_an_image_is_left_alone_and_its_work_moves_on()
    {
        await using var launcher = new DispatchLauncher { Imageless = ["spent"] };
        await using var factory = new DispatchFactory(launcher);
        await AccountAsync(factory, "spent", DateTime.UtcNow.AddHours(-2));
        var fresh = await AccountAsync(factory, "fresh", DateTime.UtcNow.AddHours(-1));
        var first = await SettledAsync(factory, await QueueAsync(factory, "first request"));
        Assert.Equal(RunStatus.Completed, first.Status);
        Assert.Equal(fresh, first.AccountId);
        Assert.Null(first.ErrorMessage);
        var second = await SettledAsync(factory, await QueueAsync(factory, "second request"));
        Assert.Equal(RunStatus.Completed, second.Status);
        Assert.Equal(fresh, second.AccountId);
        // Asked once, then left alone for the rest of the day.
        Assert.Equal(1, launcher.Launched.Count(x => x == "spent"));
    }

    [Fact]
    public async Task With_no_other_account_to_try_the_generation_fails_with_the_reason()
    {
        await using var launcher = new DispatchLauncher { Imageless = ["only"] };
        await using var factory = new DispatchFactory(launcher);
        await AccountAsync(factory, "only");
        var settled = await SettledAsync(factory, await QueueAsync(factory, "nowhere else to go"));
        Assert.Equal(RunStatus.Failed, settled.Status);
        Assert.Equal("NoImageReturned", settled.ErrorMessage);
    }

    [Fact]
    public async Task A_retry_opens_its_own_conversation_instead_of_continuing_the_spent_accounts_one()
    {
        await using var launcher = new DispatchLauncher { Imageless = ["spent"] };
        await using var factory = new DispatchFactory(launcher);
        await AccountAsync(factory, "spent", DateTime.UtcNow.AddHours(-2));
        var fresh = await AccountAsync(factory, "fresh", DateTime.UtcNow.AddHours(-1));
        const string thread = "https://chatgpt.com/c/the-spent-accounts-thread";
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var owner = await OwnerAsync(factory);
            var project = new ImageProject { UserId = owner, Title = "Edit that has to move", ConversationUrl = thread };
            var parent = new Generation { ProjectId = project.Id, UserId = owner, Prompt = "The original", Status = RunStatus.Completed, ConversationUrl = thread };
            parent.LocalImagePath = await scope.ServiceProvider.GetRequiredService<IImageStorage>().SaveAsync(project.Id, parent.Id, Convert.FromBase64String(Png), default);
            var child = new Generation { ProjectId = project.Id, UserId = owner, Prompt = "Make it warmer", Operation = Operation.Edit, ParentGenerationId = parent.Id };
            db.Projects.Add(project); db.Generations.AddRange(parent, child); await db.SaveChangesAsync();
            id = child.Id;
        }
        var settled = await SettledAsync(factory, id);
        Assert.Equal(RunStatus.Completed, settled.Status);
        Assert.Equal(fresh, settled.AccountId);
        // The prompt was already answered once; it may never go back into that conversation, and that conversation is not even this account's.
        Assert.Equal(Operation.Branch, settled.Operation);
        Assert.NotEqual(thread, settled.ConversationUrl);
        Assert.StartsWith("https://chatgpt.com/c/", settled.ConversationUrl);
    }
}
