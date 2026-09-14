using System.Net;
using System.Net.Sockets;
using Loomi.BrowserAutomation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Loomi.Tests;
public class ConnectionStateTests
{
    private const string ChallengePage = """
        <html><head><title>Just a moment...</title></head><body>
        <div id="challenge-running">Verify you are human</div>
        <form id="challenge-form"></form>
        </body></html>
        """;

    private static (BrowserSession Session, string Root) Build(FixtureLauncher launcher, BrowserOptions? options = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "loomi-state-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Root"] = root }).Build();
        options ??= new BrowserOptions();
        options.Headless = true;
        return (new BrowserSession("default", Options.Create(options), config, launcher), root);
    }

    [Fact]
    public async Task A_cloudflare_challenge_is_reported_as_verification_not_as_a_missing_login()
    {
        await using var launcher = new FixtureLauncher { Body = ChallengePage };
        var (session, root) = Build(launcher);
        await using (session) Assert.Equal("VerificationRequired", (await session.ConnectAsync(default)).State);
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task The_remote_desktop_link_is_withheld_when_nothing_serves_it()
    {
        await using var launcher = new FixtureLauncher();
        var (session, root) = Build(launcher, new BrowserOptions { DesktopPort = ClosedPort() });
        await using (session) Assert.Null((await session.ConnectAsync(default)).DesktopUrl);
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task The_remote_desktop_link_is_offered_when_something_is_listening()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await using var launcher = new FixtureLauncher();
            var (session, root) = Build(launcher, new BrowserOptions { DesktopPort = ((IPEndPoint)listener.LocalEndpoint).Port });
            await using (session) Assert.Contains("vnc.html", (await session.ConnectAsync(default)).DesktopUrl);
            Directory.Delete(root, true);
        }
        finally { listener.Stop(); }
    }

    private static int ClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
