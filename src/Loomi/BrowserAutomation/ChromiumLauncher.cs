using Microsoft.Playwright;
namespace Loomi.BrowserAutomation;
public interface IChromiumLauncher
{
    Task<IBrowserContext> LaunchAsync(string profile, BrowserOptions options);
}
public sealed class ChromiumLauncher : IChromiumLauncher, IDisposable
{
    private IPlaywright? playwright;
    public async Task<IBrowserContext> LaunchAsync(string profile, BrowserOptions options)
    {
        playwright ??= await Playwright.CreateAsync();
        return await playwright.Chromium.LaunchPersistentContextAsync(profile, new()
        {
            Headless = options.Headless, ExecutablePath = options.ExecutablePath,
            AcceptDownloads = true, ViewportSize = new() { Width = 1440, Height = 1000 },
            Args = ["--disable-dev-shm-usage"]
        });
    }
    public void Dispose() => playwright?.Dispose();
}
