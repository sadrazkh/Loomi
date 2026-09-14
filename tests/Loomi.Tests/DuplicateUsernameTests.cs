using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Loomi.Security;
using Xunit;

namespace Loomi.Tests;
public class DuplicateUsernameTests
{
    private static async Task<HttpClient> OwnerAsync(AppFactory factory)
    {
        var client = factory.CreateClient();
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        await client.PostAsJsonAsync("/api/session/login", new { username = OwnerBootstrap.Username, password = AppFactory.Key });
        session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        return client;
    }

    [Fact]
    public async Task Racing_the_same_username_yields_one_winner_and_conflicts_never_a_server_error()
    {
        using var factory = new AppFactory();
        using var client = await OwnerAsync(factory);
        var attempts = Enumerable.Range(0, 6).Select(_ =>
            client.PostAsJsonAsync("/api/users", new { username = "sara", password = "sara-strong-pass", dailyQuota = 10 }));
        var results = await Task.WhenAll(attempts);
        var codes = results.Select(r => r.StatusCode).ToList();
        Assert.Single(codes, HttpStatusCode.Created);
        // The pre-check cannot see a row another request has not committed yet, so the unique index has to be translated too.
        Assert.All(codes.Where(c => c != HttpStatusCode.Created), c => Assert.Equal(HttpStatusCode.Conflict, c));
        foreach (var conflict in results.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            Assert.Equal("DuplicateUsername", (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>("/api/users")).GetArrayLength());
    }
}
