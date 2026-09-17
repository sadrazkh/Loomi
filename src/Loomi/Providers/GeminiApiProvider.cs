using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Loomi.BrowserAutomation;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.Extensions.Options;
namespace Loomi.Providers;
public class GeminiOptions
{
    /// <summary>Google's Interactions API. Read from https://ai.google.dev/gemini-api/docs/image-generation on 2026-09-17; the model list there also
    /// carries gemini-3.1-flash-lite-image, gemini-3-pro-image and gemini-2.5-flash-image, any of which can be named here instead.</summary>
    public string Endpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta/interactions";
    public string Model { get; set; } = "gemini-3.1-flash-image";
    /// <summary>Sent as Api-Revision when set. The documentation mentions the header without saying it is required, so nothing is invented here.</summary>
    public string? ApiRevision { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
}
/// <summary>Gemini through Google's own API. The seam is the same one the browser sits behind, so the dispatcher, the credits and the UI treat it as
/// just another provider — it simply has no browser, no profile and no conversation to link to.</summary>
public sealed class GeminiApiProvider(IHttpClientFactory clients, AccountSecrets secrets, IOptions<GeminiOptions> options) : IImageProvider
{
    public const string ClientName = "gemini";
    private readonly GeminiOptions settings = options.Value;
    /// <summary>Accounts whose key the service refused. Remembered so the dispatcher passes over a dead key instead of spending a run on it every time round.</summary>
    private readonly ConcurrentDictionary<Guid, byte> rejected = new();
    private readonly ConcurrentDictionary<Guid, byte> busy = new();
    public Provider Provider => Provider.Gemini;
    public bool IsBusy(ProviderAccount account) => busy.ContainsKey(account.Id);
    public string StateOf(ProviderAccount account) =>
        secrets.Reveal(account.Secret) is null ? "Disconnected" : rejected.ContainsKey(account.Id) ? "LoginRequired" : "Connected";
    /// <summary>There is no browser to show, so there is nothing for the remote desktop to display.</summary>
    public Task<string?> DesktopUrlAsync() => Task.FromResult<string?>(null);
    public Task<ConnectionStatus> StatusAsync(ProviderAccount account) => Task.FromResult(new ConnectionStatus(StateOf(account), IsBusy(account), null));
    /// <summary>Asks the service about the model rather than for an image: it proves the key without spending anything.</summary>
    public async Task<ConnectionStatus> ConnectAsync(ProviderAccount account, CancellationToken ct)
    {
        if (secrets.Reveal(account.Secret) is not { } key) return new("Disconnected", false, null);
        using var request = new HttpRequestMessage(HttpMethod.Get, ModelUrl());
        Authorize(request, key);
        try
        {
            var response = await Client().SendAsync(request, ct);
            if (Rejects(response.StatusCode)) rejected[account.Id] = 0; else rejected.TryRemove(account.Id, out _);
        }
        // A service that cannot be reached is not a key that was refused; the account keeps what it had.
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { }
        return new(StateOf(account), IsBusy(account), null);
    }
    public Task ResetAsync(ProviderAccount account, CancellationToken ct) { rejected.TryRemove(account.Id, out _); return Task.CompletedTask; }
    public Task DiscardAsync(Guid accountId) { rejected.TryRemove(accountId, out _); busy.TryRemove(accountId, out _); return Task.CompletedTask; }

    public async Task<BrowserResult> RunAsync(ProviderAccount account, GenerationRequest request, ReportStatus report, CancellationToken ct)
    {
        if (secrets.Reveal(account.Secret) is not { } key) throw new InvalidOperationException("LoginRequired");
        if (!busy.TryAdd(account.Id, 0)) throw new InvalidOperationException("BrowserBusy");
        try
        {
            await report(RunStatus.SendingPrompt);
            var input = new List<object> { new { type = "text", text = request.Prompt } };
            foreach (var path in request.InputPaths)
            {
                var bytes = await File.ReadAllBytesAsync(path, ct);
                // Judged by its bytes here too: the provider is told the type the file actually is, not the one its name claims.
                var extension = ImageStorage.ExtensionFor(bytes) ?? throw new InvalidOperationException("InvalidImage");
                input.Add(new { type = "image", mime_type = ImageStorage.ContentTypeFor("input." + extension), data = Convert.ToBase64String(bytes) });
            }
            using var call = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint) { Content = JsonContent.Create(new { model = settings.Model, input }) };
            Authorize(call, key);
            await report(RunStatus.GeneratingImage);
            HttpResponseMessage response;
            // Nothing from the transport is allowed to escape either: a request URL or a proxy error can carry more than the caller should read.
            try { response = await Client().SendAsync(call, ct); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { throw new InvalidOperationException("GenerationTimeout"); }
            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException(CodeFor(response.StatusCode, body, account));
                rejected.TryRemove(account.Id, out _);
                await report(RunStatus.DownloadingImage);
                // A reply that carries no picture is this account answering without generating — the same thing the browser provider calls NoImageReturned,
                // so the dispatcher parks the account and moves the work rather than failing it outright.
                var image = ImageIn(body) ?? throw new InvalidOperationException("NoImageReturned");
                // Gemini has no conversation of its own; the row says so rather than inventing a link the UI would offer to open.
                return new(image, null);
            }
        }
        finally { busy.TryRemove(account.Id, out _); }
    }
    private HttpClient Client()
    {
        var client = clients.CreateClient(ClientName);
        client.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        return client;
    }
    private string ModelUrl() => settings.Endpoint[..(settings.Endpoint.LastIndexOf('/') + 1)] + "models/" + settings.Model;
    /// <summary>In a header, never a query parameter: a key in a URL is written to every log and proxy between here and Google.</summary>
    private void Authorize(HttpRequestMessage request, string key)
    {
        request.Headers.TryAddWithoutValidation("x-goog-api-key", key);
        if (!string.IsNullOrWhiteSpace(settings.ApiRevision)) request.Headers.TryAddWithoutValidation("Api-Revision", settings.ApiRevision);
    }
    private static bool Rejects(HttpStatusCode status) => status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
    /// <summary>Maps a refusal to one of the codes the rest of Loomi already knows, so a Gemini account behaves like a browser account that ran out.
    /// Only the shape of the error is read, never its words: the body can quote the key and the prompt back.</summary>
    private string CodeFor(HttpStatusCode status, string body, ProviderAccount account)
    {
        var code = Classify(status, body);
        // Parking follows the verdict, not the status line: Google answers a dead key with 400, and an account left "Connected" after that
        // would be handed the next run, and the one after, for as long as the key stayed wrong.
        if (code == "LoginRequired") rejected[account.Id] = 0;
        return code;
    }
    private static string Classify(HttpStatusCode status, string body)
    {
        if (Rejects(status)) return "LoginRequired";
        // Running out is not a broken key: the account stays usable and tomorrow's work goes to it again.
        if (status == HttpStatusCode.TooManyRequests) return "NoImageReturned";
        if (status != HttpStatusCode.BadRequest) return "AutomationFailed";
        var signal = Signal(body);
        return signal.Contains("safety", StringComparison.OrdinalIgnoreCase) || signal.Contains("blocked", StringComparison.OrdinalIgnoreCase) || signal.Contains("prohibited", StringComparison.OrdinalIgnoreCase) ? "ContentBlocked"
            : signal.Contains("api_key", StringComparison.OrdinalIgnoreCase) || signal.Contains("api key", StringComparison.OrdinalIgnoreCase) ? "LoginRequired"
            : "AutomationFailed";
    }
    /// <summary>The machine-readable part of an error and nothing else. A live call with a bad key showed why this cannot stop at the top level: Google
    /// answers 400 with a numeric code, status INVALID_ARGUMENT, and the reason that actually names the problem — API_KEY_INVALID — inside details.
    /// The message is deliberately not read: it is free text and can quote the prompt or the key back.</summary>
    private static string Signal(string body)
    {
        try
        {
            var root = JsonDocument.Parse(body).RootElement;
            // Some Google endpoints answer with a single-element array around the envelope.
            if (root.ValueKind == JsonValueKind.Array) root = root.EnumerateArray().FirstOrDefault();
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error)) return "";
            var parts = new List<string?>();
            foreach (var name in new[] { "code", "status", "reason" })
                if (error.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) parts.Add(value.GetString());
            if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                foreach (var detail in details.EnumerateArray())
                    if (detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String) parts.Add(reason.GetString());
            return string.Join(' ', parts.Where(x => x != null));
        }
        catch (JsonException) { return ""; }
    }
    /// <summary>The first image block of a model_output step: the documented place generated media appears in an interaction's timeline.</summary>
    private static byte[]? ImageIn(string body)
    {
        try
        {
            var root = JsonDocument.Parse(body).RootElement;
            if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) return null;
            foreach (var step in steps.EnumerateArray())
            {
                if (!step.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                foreach (var block in content.EnumerateArray())
                    if (block.TryGetProperty("type", out var type) && type.GetString() == "image"
                        && block.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String
                        && Convert.TryFromBase64String(data.GetString()!, new byte[data.GetString()!.Length], out _))
                        return Convert.FromBase64String(data.GetString()!);
            }
            return null;
        }
        catch (JsonException) { return null; }
    }
}
