using Microsoft.Playwright;
namespace Loomi.BrowserAutomation;
public interface IChromiumLauncher
{
    Task<IBrowserContext> LaunchAsync(string profile, BrowserOptions options);
}
public sealed class ChromiumLauncher(ILogger<ChromiumLauncher> logger) : IChromiumLauncher, IDisposable
{
    private IPlaywright? playwright;
    /// <summary>Browsers to try in order. Playwright's own build first; installed channels only when no explicit executable is configured.</summary>
    public static string?[] Candidates(BrowserOptions options) =>
        string.IsNullOrWhiteSpace(options.ExecutablePath) ? [null, .. options.Channels] : [null];
    public async Task<IBrowserContext> LaunchAsync(string profile, BrowserOptions options)
    {
        playwright ??= await Playwright.CreateAsync();
        PlaywrightException? failure = null;
        foreach (var channel in Candidates(options))
        {
            try { return await LaunchAsync(profile, options, channel); }
            catch (PlaywrightException ex) { failure ??= ex; logger.LogWarning("Chromium ({Browser}) could not start on this host.", channel ?? "playwright build"); }
        }
        throw failure!;
    }
    private Task<IBrowserContext> LaunchAsync(string profile, BrowserOptions options, string? channel) =>
        playwright!.Chromium.LaunchPersistentContextAsync(profile, new()
        {
            Headless = options.Headless, ExecutablePath = options.ExecutablePath, Channel = channel,
            AcceptDownloads = true, ViewportSize = new() { Width = 1440, Height = 1000 },
            Args = options.Args, IgnoreDefaultArgs = options.IgnoreDefaultArgs
        });
    public void Dispose() => playwright?.Dispose();
}
