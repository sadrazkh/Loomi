using System.Net;
using Loomi.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Loomi.Tests;
/// <summary>Harbora's Traefik is reattached to the app's network with a new address after every restart, so it can only be trusted by range. Untrusted,
/// every tester behind it shares one address: one login rate limit for all of them, and a WebSocket origin check that sees http behind https.</summary>
public class TrustedProxiesTests
{
    private sealed class BehindProxy(string? networks) : AppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (networks is not null) builder.UseSetting("Security:KnownNetworks:0", networks);
        }
    }
    private static async Task<int> LoginAsync(AppFactory factory, string client)
    {
        var context = await factory.Server.SendAsync(c =>
        {
            c.Request.Method = "POST"; c.Request.Path = "/api/session/login";
            c.Connection.RemoteIpAddress = IPAddress.Parse("172.18.0.5");   // Traefik, on the app's network
            c.Request.Headers["X-Forwarded-For"] = client;
            c.Request.ContentType = "application/json";
            c.Request.Body = new MemoryStream("""{"username":"owner","password":"wrong"}"""u8.ToArray());
        });
        return context.Response.StatusCode;
    }

    [Theory]
    [InlineData("172.16.0.0/12", StatusCodes.Status400BadRequest)]   // trusted: the second person has a window of their own
    [InlineData(null, StatusCodes.Status429TooManyRequests)]          // untrusted: one person's typos lock everyone out
    public async Task Two_people_behind_the_proxy_get_a_login_window_each_only_when_its_network_is_trusted(string? networks, int secondPerson)
    {
        using var factory = new BehindProxy(networks);
        for (var i = 0; i < 6; i++) Assert.Equal(StatusCodes.Status400BadRequest, await LoginAsync(factory, "203.0.113.10"));
        Assert.Equal(StatusCodes.Status429TooManyRequests, await LoginAsync(factory, "203.0.113.10"));
        Assert.Equal(secondPerson, await LoginAsync(factory, "198.51.100.20"));
    }

    /// <summary>Traefik says wss for a WebSocket behind https. Taken as it came, the origin check refused the remote desktop and SignalR alike.</summary>
    [Theory]
    [InlineData("https://localhost", "401")]   // past the origin check; refused only because nobody signed in
    [InlineData("https://evil.example", "403")]
    public async Task A_socket_the_proxy_calls_wss_is_judged_against_the_https_page_that_opened_it(string origin, string status)
    {
        using var factory = new BehindProxy("172.16.0.0/12");
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r =>
        {
            r.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("172.28.0.2");
            r.Headers["X-Forwarded-Proto"] = "wss"; r.Headers["X-Forwarded-For"] = "203.0.113.10"; r.Headers.Origin = origin;
        };
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(new Uri("ws://localhost/hubs/status"), CancellationToken.None));
        Assert.Contains(status, refused.Message);
    }

    [Theory]
    [InlineData("wss", "https")]
    [InlineData("ws", "http")]
    [InlineData("https", "https")]
    [InlineData("http", "http")]
    public void A_socket_scheme_is_read_as_the_page_scheme(string forwarded, string page) => Assert.Equal(page, TrustedProxies.PageScheme(forwarded));

    [Fact]
    public void A_mistyped_network_stops_the_app_and_says_which_value()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Security:KnownNetworks:0"] = "172.16.0.0/33" }).Build();
        Assert.Contains("172.16.0.0/33", Assert.Throws<InvalidOperationException>(() => TrustedProxies.Read(config)).Message);
    }

    [Fact]
    public void Proxies_and_networks_are_both_read()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:KnownProxies:0"] = "172.17.0.1", ["Security:KnownNetworks:0"] = "10.0.0.0/8", ["Security:KnownNetworks:1"] = "172.16.0.0/12"
        }).Build();
        var trust = TrustedProxies.Read(config);
        Assert.Equal([IPAddress.Parse("172.17.0.1")], trust.Proxies);
        Assert.Equal([System.Net.IPNetwork.Parse("10.0.0.0/8"), System.Net.IPNetwork.Parse("172.16.0.0/12")], trust.Networks);
    }
}
