using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.Models;
using Loomi.Providers;
using Loomi.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Loomi.Tests;
/// <summary>Gemini without Gemini. Every call is answered from here, so no test needs a key, a network or a bill; it also records what went out, which is the only way to see that the key travelled in a header and the images travelled inline.</summary>
public sealed class FakeGemini(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler, IHttpClientFactory
{
    public const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nS8AAAAASUVORK5CYII=";
    public List<string> Urls { get; } = [];
    public List<string> Methods { get; } = [];
    public List<string> Keys { get; } = [];
    public List<string> Bodies { get; } = [];
    public static FakeGemini Returning(HttpStatusCode status, string body) => new(_ => (status, body));
    /// <summary>The documented success shape: the bytes live in a content block of a step, and `output_image` is the SDK's shortcut to the same thing.</summary>
    public static string Image(string base64 = Png) =>
        $$"""{"id":"v1_test","object":"interaction","model":"gemini-3.1-flash-image","status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"Here you go."},{"type":"image","mime_type":"image/png","data":"{{base64}}"}]}]}""";
    public static string Error(string code, string message) => """{"error":{"code":"C","message":M}}""".Replace("\"C\"", JsonSerializer.Serialize(code)).Replace("M", JsonSerializer.Serialize(message));
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Urls.Add(request.RequestUri!.ToString());
        Methods.Add(request.Method.Method);
        Keys.Add(request.Headers.TryGetValues("x-goog-api-key", out var values) ? string.Concat(values) : "");
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        var (status, body) = answer(request);
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
}
/// <summary>The dispatcher over both providers at once: real browsers for ChatGPT, this handler for Gemini.</summary>
public class GeminiFactory(IChromiumLauncher launcher, FakeGemini gemini) : DispatchFactory(launcher)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.AddHttpClient(GeminiApiProvider.ClientName).ConfigurePrimaryHttpMessageHandler(() => gemini));
    }
}
public class GeminiTests
{
    private const string Key = "AIzaSy-this-is-not-a-real-key-0000000000";
    private static readonly byte[] Bytes = Convert.FromBase64String(FakeGemini.Png);
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0];
    private static AccountSecrets Secrets() => new(new ServiceCollection().AddDataProtection().Services.BuildServiceProvider().GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>());
    private static (GeminiApiProvider Provider, ProviderAccount Account) Ready(FakeGemini gemini, string? key = Key)
    {
        var secrets = Secrets();
        var provider = new GeminiApiProvider(gemini, secrets, Options.Create(new GeminiOptions()));
        return (provider, new ProviderAccount { Provider = Provider.Gemini, Kind = AccountKind.ApiKey, Label = "Gemini", Secret = key is null ? null : secrets.Protect(key) });
    }
    private static GenerationRequest Ask(string prompt = "a quiet harbour at dawn", params string[] inputs) =>
        new(Provider.Gemini, Operation.Generate, prompt, inputs, null);
    /// <summary>Files on disk, because that is what the dispatcher hands a provider.</summary>
    private static async Task<(string Root, string[] Paths)> FilesAsync(params byte[][] images)
    {
        var root = Path.Combine(Path.GetTempPath(), "loomi-gemini-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var paths = new List<string>();
        foreach (var (image, i) in images.Select((x, i) => (x, i)))
        {
            var path = Path.Combine(root, $"in{i}.bin");
            await File.WriteAllBytesAsync(path, image);
            paths.Add(path);
        }
        return (root, [.. paths]);
    }
    private static async Task<string> FailureAsync(GeminiApiProvider provider, ProviderAccount account, GenerationRequest? request = null)
    {
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => provider.RunAsync(account, request ?? Ask(), (_, _) => Task.CompletedTask, default));
        return Loomi.Services.GenerationWorker.ErrorCodeFor(thrown);
    }

    [Fact]
    public async Task A_generated_image_comes_back_as_bytes_with_no_chat_to_link_to()
    {
        using var gemini = FakeGemini.Returning(HttpStatusCode.OK, FakeGemini.Image());
        var (provider, account) = Ready(gemini);
        var steps = new List<RunStatus>();
        var result = await provider.RunAsync(account, Ask(), (status, _) => { steps.Add(status); return Task.CompletedTask; }, default);
        Assert.Equal(Bytes, result.Image);
        // Gemini has no conversation to send anyone to, and the row has to say so rather than invent a link.
        Assert.Null(result.ConversationUrl);
        // The same three steps a browser run reports, so the UI needs no special case for a provider without a browser.
        Assert.Equal([RunStatus.SendingPrompt, RunStatus.GeneratingImage, RunStatus.DownloadingImage], steps);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/interactions", Assert.Single(gemini.Urls));
        Assert.Equal("POST", Assert.Single(gemini.Methods));
        // The model name is read from Google's documentation, never invented: a plausible id passes every test and fails in production.
        Assert.Contains("\"model\":\"gemini-3.1-flash-image\"", Assert.Single(gemini.Bodies));
        Assert.Contains("a quiet harbour at dawn", gemini.Bodies[0]);
        Assert.Equal(Key, Assert.Single(gemini.Keys));
        Assert.Null(await provider.DesktopUrlAsync());
        Assert.False(provider.IsBusy(account));
        Assert.Equal("Connected", provider.StateOf(account));
    }

    [Fact]
    public async Task Reference_images_travel_inline_with_the_prompt_in_the_order_given()
    {
        using var gemini = FakeGemini.Returning(HttpStatusCode.OK, FakeGemini.Image());
        var (provider, account) = Ready(gemini);
        var (root, paths) = await FilesAsync(Bytes, Jpeg);
        try { await provider.RunAsync(account, Ask("combine these", paths), (_, _) => Task.CompletedTask, default); }
        finally { Directory.Delete(root, true); }
        var input = JsonDocument.Parse(Assert.Single(gemini.Bodies)).RootElement.GetProperty("input").EnumerateArray().ToList();
        Assert.Equal(3, input.Count);
        Assert.Equal("text", input[0].GetProperty("type").GetString());
        Assert.Equal("combine these", input[0].GetProperty("text").GetString());
        Assert.Equal(["image", "image"], input.Skip(1).Select(x => x.GetProperty("type").GetString()));
        // Judged by their bytes, like every other image in Loomi, not by what the file was called.
        Assert.Equal(["image/png", "image/jpeg"], input.Skip(1).Select(x => x.GetProperty("mime_type").GetString()));
        Assert.Equal([Convert.ToBase64String(Bytes), Convert.ToBase64String(Jpeg)], input.Skip(1).Select(x => x.GetProperty("data").GetString()));
    }

    [Fact]
    public async Task A_spent_key_reads_as_no_image_so_the_work_moves_to_another_account()
    {
        using var quota = FakeGemini.Returning(HttpStatusCode.TooManyRequests, FakeGemini.Error("quota_exceeded", "You have exceeded your daily quota."));
        var (spent, account) = Ready(quota);
        Assert.Equal("NoImageReturned", await FailureAsync(spent, account));
        // Running out is not a broken key: the account stays usable and tomorrow's work goes to it again.
        Assert.Equal("Connected", spent.StateOf(account));
        using var silent = FakeGemini.Returning(HttpStatusCode.OK, """{"id":"v1_test","object":"interaction","status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"I can describe it instead."}]}]}""");
        var (answered, other) = Ready(silent);
        // A reply with no picture in it is the same thing the ChatGPT provider calls NoImageReturned, and it has to move the same way.
        Assert.Equal("NoImageReturned", await FailureAsync(answered, other));
    }

    [Fact]
    public async Task A_rejected_key_parks_the_account_until_someone_replaces_it()
    {
        using var gemini = FakeGemini.Returning(HttpStatusCode.Unauthorized, FakeGemini.Error("authentication", "The API key is missing, invalid, or expired."));
        var (provider, account) = Ready(gemini);
        Assert.Equal("LoginRequired", await FailureAsync(provider, account));
        // The dispatcher passes over LoginRequired, so a dead key stops costing a run each time the queue comes round.
        Assert.Equal("LoginRequired", provider.StateOf(account));
        await provider.ResetAsync(account, default);
        Assert.Equal("Connected", provider.StateOf(account));
        Assert.Equal("LoginRequired", await FailureAsync(provider, account));
        await provider.DiscardAsync(account.Id);
        Assert.Equal("Connected", provider.StateOf(account));
    }

    [Fact]
    public async Task A_blocked_prompt_is_named_and_anything_unrecognised_stays_generic()
    {
        using var blocked = FakeGemini.Returning(HttpStatusCode.BadRequest, FakeGemini.Error("image_safety", "The prompt was blocked."));
        var (safety, account) = Ready(blocked);
        Assert.Equal("ContentBlocked", await FailureAsync(safety, account));
        using var broken = FakeGemini.Returning(HttpStatusCode.InternalServerError, FakeGemini.Error("api_error", "Something went wrong."));
        var (server, other) = Ready(broken);
        Assert.Equal("AutomationFailed", await FailureAsync(server, other));
    }

    [Fact]
    public async Task Neither_the_key_nor_the_services_own_words_reach_the_user()
    {
        const string prompt = "a harbour nobody else should read about";
        using var gemini = FakeGemini.Returning(HttpStatusCode.BadRequest, FakeGemini.Error("invalid_request", $"Key {Key} rejected while generating \"{prompt}\"."));
        var (provider, account) = Ready(gemini);
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => provider.RunAsync(account, Ask(prompt), (_, _) => Task.CompletedTask, default));
        // The body comes back with the key and the prompt in it; what leaves this method is a code and nothing else.
        Assert.DoesNotContain(Key, thrown.ToString());
        Assert.DoesNotContain(prompt, thrown.ToString());
        Assert.Equal("AutomationFailed", Loomi.Services.GenerationWorker.ErrorCodeFor(thrown));
    }

    [Fact]
    public async Task An_account_with_no_key_is_disconnected_and_runs_nothing()
    {
        using var gemini = FakeGemini.Returning(HttpStatusCode.OK, FakeGemini.Image());
        var (provider, account) = Ready(gemini, key: null);
        Assert.Equal("Disconnected", provider.StateOf(account));
        Assert.Equal("LoginRequired", await FailureAsync(provider, account));
        // Nothing was ever sent: there was nothing to send it with.
        Assert.Empty(gemini.Urls);
        var status = await provider.StatusAsync(account);
        Assert.Equal("Disconnected", status.State);
        Assert.Null(status.DesktopUrl);
    }

    [Fact]
    public async Task Connecting_checks_the_key_without_generating_anything()
    {
        using var good = FakeGemini.Returning(HttpStatusCode.OK, """{"name":"models/gemini-3.1-flash-image"}""");
        var (provider, account) = Ready(good);
        Assert.Equal("Connected", (await provider.ConnectAsync(account, default)).State);
        Assert.Equal("GET", Assert.Single(good.Methods));
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-image", Assert.Single(good.Urls));
        // A key is worth nothing in a query string: that is the one place it would be written to every log and proxy on the way.
        Assert.DoesNotContain(Key, good.Urls[0]);
        Assert.Equal(Key, Assert.Single(good.Keys));
        using var bad = FakeGemini.Returning(HttpStatusCode.Forbidden, FakeGemini.Error("permission_denied", "no."));
        var (refused, rejected) = Ready(bad);
        Assert.Equal("LoginRequired", (await refused.ConnectAsync(rejected, default)).State);
    }

    [Fact]
    public async Task A_gemini_account_needs_a_key_and_a_chatgpt_account_must_not_carry_one()
    {
        using var factory = new AppFactory();
        using var client = await OwnerAsync(factory);
        var missing = await client.PostAsJsonAsync("/api/auth/accounts", new { label = "Gemini", provider = "Gemini" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("ApiKeyRequired", await ErrorAsync(missing));
        var unwanted = await client.PostAsJsonAsync("/api/auth/accounts", new { label = "Browser", provider = "ChatGPT", apiKey = Key });
        Assert.Equal(HttpStatusCode.BadRequest, unwanted.StatusCode);
        Assert.Equal("ApiKeyNotAllowed", await ErrorAsync(unwanted));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Accounts.CountAsync());
    }

    [Fact]
    public async Task A_stored_key_is_only_ever_shown_as_its_last_four_characters()
    {
        using var factory = new AppFactory();
        using var client = await OwnerAsync(factory);
        var created = await client.PostAsJsonAsync("/api/auth/accounts", new { label = "Gemini", provider = "Gemini", apiKey = Key });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var account = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ApiKey", account.GetProperty("kind").GetString());
        Assert.Equal("Connected", account.GetProperty("state").GetString());
        Assert.Equal("••••" + Key[^4..], account.GetProperty("keyHint").GetString());
        var listed = await client.GetStringAsync("/api/auth/accounts");
        Assert.DoesNotContain(Key, listed);
        Assert.DoesNotContain(Key[4..], listed);
        Assert.Contains("••••" + Key[^4..], listed);
        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Accounts.SingleAsync();
        // At rest it is sealed with its own purpose, so a copy of the database is not a copy of the key.
        Assert.NotNull(stored.Secret);
        Assert.DoesNotContain(Key, stored.Secret);
        Assert.Null(stored.ProfileDirectory);
        Assert.Equal(Key, scope.ServiceProvider.GetRequiredService<AccountSecrets>().Reveal(stored.Secret));
    }

    [Fact]
    public async Task A_member_cannot_add_an_api_account_or_read_the_mask()
    {
        using var factory = new AppFactory();
        await MemberAsync(factory, "sara", "sara-strong-pass");
        using var member = await SignInAsync(factory, "sara", "sara-strong-pass");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/auth/accounts", new { label = "Mine", provider = "Gemini", apiKey = Key })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/auth/accounts")).StatusCode);
    }

    [Fact]
    public async Task Work_is_priced_and_provided_by_the_provider_it_asked_for()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory, "sara", "sara-strong-pass");
        using var client = await SignInAsync(factory, "sara", "sara-strong-pass");
        var project = await ProjectAsync(factory, sara, "two providers");
        await AccountsAsync(factory, Provider.ChatGPT, Provider.Gemini);
        var byDefault = await Queued(client, project, new { prompt = "no provider named" });
        var gemini = await Queued(client, project, new { prompt = "on Gemini", provider = "Gemini" });
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // An existing caller that names no provider still gets ChatGPT, and pays the ChatGPT price.
        Assert.Equal((Provider.ChatGPT, 10), await db.Generations.Where(g => g.Id == byDefault).Select(g => new ValueTuple<Provider, int>(g.Provider, g.CreditCost)).SingleAsync());
        Assert.Equal((Provider.Gemini, 5), await db.Generations.Where(g => g.Id == gemini).Select(g => new ValueTuple<Provider, int>(g.Provider, g.CreditCost)).SingleAsync());
        // Edit and branch belong to the parent's world and never change providers underneath it.
        await db.Generations.Where(g => g.Id == gemini).ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, RunStatus.Completed).SetProperty(g => g.LocalImagePath, "projects/x/y.png"));
        var edited = await Queued(client, project, new { prompt = "warmer" }, $"/api/generations/{gemini}/edit");
        Assert.Equal(Provider.Gemini, await db.Generations.Where(g => g.Id == edited).Select(g => g.Provider).SingleAsync());
    }

    [Fact]
    public async Task A_provider_no_connected_account_serves_is_refused_while_an_empty_workspace_still_queues()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory, "sara", "sara-strong-pass");
        using var client = await SignInAsync(factory, "sara", "sara-strong-pass");
        var project = await ProjectAsync(factory, sara, "nothing connected");
        // Nothing is connected yet: the queue takes the work and /api/auth/status is what explains the wait.
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/projects/{project}/generate", new { prompt = "later" })).StatusCode);
        await AccountsAsync(factory, Provider.ChatGPT);
        // Now that accounts exist, asking for one none of them serve is a row that would wait for ever.
        var refused = await client.PostAsJsonAsync($"/api/projects/{project}/generate", new { prompt = "on Gemini", provider = "Gemini" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("NoAccount", await ErrorAsync(refused));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Generations.CountAsync());
    }

    [Fact]
    public async Task The_dispatcher_never_hands_gemini_work_to_a_browser_or_the_reverse()
    {
        using var gemini = FakeGemini.Returning(HttpStatusCode.OK, FakeGemini.Image());
        await using var launcher = new DispatchLauncher();
        await using var factory = new GeminiFactory(launcher, gemini);
        using var client = await OwnerAsync(factory);
        var owner = await OwnerIdAsync(factory);
        var project = await ProjectAsync(factory, owner, "both providers");
        var (browser, api) = await AccountsAsync(factory, Provider.ChatGPT, Provider.Gemini);
        var onGemini = await SettledAsync(factory, await Queued(client, project, new { prompt = "on Gemini", provider = "Gemini" }));
        Assert.Equal(RunStatus.Completed, onGemini.Status);
        Assert.Equal(api, onGemini.AccountId);
        // No browser was ever started for it, and no chat link was invented for a provider that has none.
        Assert.Empty(launcher.Launched);
        Assert.Null(onGemini.ConversationUrl);
        // The model the running application uses is the one configured, not one this test made up.
        Assert.Contains("\"model\":\"gemini-3.1-flash-image\"", Assert.Single(gemini.Bodies));
        var onChatGpt = await SettledAsync(factory, await Queued(client, project, new { prompt = "on ChatGPT" }));
        Assert.Equal(RunStatus.Completed, onChatGpt.Status);
        Assert.Equal(browser, onChatGpt.AccountId);
        // The API account was never asked to do browser work.
        Assert.Single(gemini.Bodies);
        Assert.Single(launcher.Launched);
    }

    private static async Task<(Guid ChatGpt, Guid Gemini)> AccountsAsync(AppFactory factory, params Provider[] providers)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var secrets = scope.ServiceProvider.GetRequiredService<AccountSecrets>();
        var made = new Dictionary<Provider, Guid>();
        foreach (var provider in providers)
        {
            var account = provider == Provider.Gemini
                ? new ProviderAccount { Provider = provider, Kind = AccountKind.ApiKey, Label = "Gemini", Secret = secrets.Protect(Key) }
                : new ProviderAccount { Provider = provider, Kind = AccountKind.Browser, Label = "ChatGPT", ProfileDirectory = "browser" };
            db.Accounts.Add(account); made[provider] = account.Id;
        }
        await db.SaveChangesAsync();
        return (made.GetValueOrDefault(Provider.ChatGPT), made.GetValueOrDefault(Provider.Gemini));
    }
    private static async Task<Guid> Queued(HttpClient client, Guid project, object body, string? route = null)
    {
        var response = await client.PostAsJsonAsync(route ?? $"/api/projects/{project}/generate", body);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    private static async Task<Generation> SettledAsync(AppFactory factory, Guid id, int seconds = 150)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            using (var scope = factory.Services.CreateScope())
            {
                var g = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Generations.AsNoTracking().FirstAsync(x => x.Id == id);
                if (g.Status is RunStatus.Completed or RunStatus.Failed) return g;
            }
            await Task.Delay(200);
        }
        throw new TimeoutException($"Generation {id} never settled.");
    }
    private static Task<HttpClient> OwnerAsync(AppFactory factory) => SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
    private static async Task<Guid> OwnerIdAsync(AppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking().Where(u => u.NormalizedUsername == OwnerBootstrap.Username).Select(u => u.Id).SingleAsync();
    }
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
    private static async Task<Guid> MemberAsync(AppFactory factory, string username, string password, int credits = 1000)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new AppUser { Username = username, NormalizedUsername = username };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<PasswordService>().Hash(user, password);
        db.Users.Add(user);
        db.CreditEntries.Add(new CreditEntry { UserId = user.Id, Amount = credits, Kind = CreditKind.Grant });
        await db.SaveChangesAsync();
        return user.Id;
    }
    private static async Task<Guid> ProjectAsync(AppFactory factory, Guid user, string title)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var project = new ImageProject { UserId = user, Title = title };
        db.Projects.Add(project); await db.SaveChangesAsync();
        return project.Id;
    }
    private static async Task<string> ErrorAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var error = body.StartsWith('{') && JsonDocument.Parse(body).RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        Assert.True(error != null, $"{(int)response.StatusCode} {body}");
        return error!;
    }
}
