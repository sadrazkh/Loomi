using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Loomi.Tests;
/// <summary>The remote desktop is the owner's ChatGPT session itself: whoever reaches it can read every prompt in that account's history, sign it out or
/// change its settings. Signing in to Loomi is not enough to be let near it.</summary>
public class DesktopAccessTests
{
    private static async Task<HttpClient> SignInAsync(AppFactory factory, string username, string password)
    {
        var client = factory.CreateClient();
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/session/login", new { username, password })).StatusCode);
        return client;
    }
    private static async Task MemberAsync(AppFactory factory, string username, string password)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new AppUser { Username = username, NormalizedUsername = username };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<PasswordService>().Hash(user, password);
        db.Users.Add(user); await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_member_is_refused_the_remote_desktop_and_its_socket()
    {
        using var factory = new AppFactory();
        await MemberAsync(factory, "sara", "sara-strong-pass");
        using var member = await SignInAsync(factory, "sara", "sara-strong-pass");
        foreach (var path in new[] { "/desktop/vnc.html", "/desktop/websockify", "/desktop/core/rfb.js" })
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task The_owner_is_let_through_to_the_proxy()
    {
        using var factory = new AppFactory();
        using var owner = await SignInAsync(factory, "owner", AppFactory.Key);
        // Nothing listens on the desktop port in a test, so the proxy answers for itself; the point is that authorisation let the owner reach it.
        var status = (await owner.GetAsync("/desktop/vnc.html")).StatusCode;
        Assert.NotEqual(HttpStatusCode.Unauthorized, status);
        Assert.NotEqual(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task A_member_is_never_offered_a_link_they_could_not_open()
    {
        // Something really listening on the desktop port, or the link is withheld for everyone and the test proves nothing.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var factory = new DesktopFactory(((IPEndPoint)listener.LocalEndpoint).Port);
            await MemberAsync(factory, "sara", "sara-strong-pass");
            using var member = await SignInAsync(factory, "sara", "sara-strong-pass");
            using var owner = await SignInAsync(factory, "owner", AppFactory.Key);
            Assert.Equal(JsonValueKind.String, (await owner.GetFromJsonAsync<JsonElement>("/api/auth/status")).GetProperty("desktopUrl").ValueKind);
            Assert.Equal(JsonValueKind.Null, (await member.GetFromJsonAsync<JsonElement>("/api/auth/status")).GetProperty("desktopUrl").ValueKind);
        }
        finally { listener.Stop(); }
    }
}
public class DesktopFactory(int port) : AppFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Browser:DesktopPort", port.ToString());
    }
}
