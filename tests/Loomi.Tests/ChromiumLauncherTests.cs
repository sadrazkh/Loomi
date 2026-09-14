using Loomi.BrowserAutomation;
using Xunit;

namespace Loomi.Tests;
public class ChromiumLauncherTests
{
    [Fact]
    public void Playwrights_own_build_is_tried_first_then_the_configured_channels()
        => Assert.Equal<string?[]>([null, "chrome", "msedge"], ChromiumLauncher.Candidates(new BrowserOptions()));

    [Fact]
    public void An_explicit_executable_is_honoured_on_its_own()
        => Assert.Equal<string?[]>([null], ChromiumLauncher.Candidates(new BrowserOptions { ExecutablePath = @"C:\chromium\chrome.exe" }));

    [Fact]
    public void Clearing_the_channels_leaves_no_fallback()
        => Assert.Equal<string?[]>([null], ChromiumLauncher.Candidates(new BrowserOptions { Channels = [] }));
}
