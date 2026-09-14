using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Loomi.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Loomi.Tests;
public class UnconfiguredFactory(string environment, string? accessKey) : WebApplicationFactory<Program>
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "loomi-test-" + Guid.NewGuid());
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("Security:AccessKey", accessKey ?? string.Empty);
        builder.UseSetting("Storage:Root", Root);
        builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
public class DevelopmentAccessTests
{
    [Fact]
    public async Task Development_starts_without_a_configured_key_and_accepts_the_built_in_one()
    {
        using var factory = new UnconfiguredFactory("Development", null); using var client = factory.CreateClient();
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.False(session.GetProperty("authenticated").GetBoolean());
        Assert.Equal(DevelopmentAccess.Key, session.GetProperty("devAccessKey").GetString());
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        var login = await client.PostAsJsonAsync("/api/session/login", new { accessKey = DevelopmentAccess.Key });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task Development_does_not_reveal_a_real_key_that_the_owner_configured()
    {
        using var factory = new AppFactory(); using var client = factory.CreateClient();
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Equal(JsonValueKind.Null, session.GetProperty("devAccessKey").ValueKind);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/session/login", new { accessKey = DevelopmentAccess.Key })).StatusCode);
    }

    [Fact]
    public void The_built_in_key_is_refused_outside_development()
    {
        using var factory = new UnconfiguredFactory("Production", DevelopmentAccess.Key);
        var error = Assert.ThrowsAny<Exception>(factory.CreateClient);
        Assert.Contains("development access key", Flatten(error), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_still_refuses_to_start_without_a_key()
    {
        using var factory = new UnconfiguredFactory("Production", null);
        var error = Assert.ThrowsAny<Exception>(factory.CreateClient);
        Assert.Contains("Security__AccessKey", Flatten(error), StringComparison.Ordinal);
    }

    private static string Flatten(Exception error) => error.InnerException is null ? error.Message : error.Message + " " + Flatten(error.InnerException);
}
