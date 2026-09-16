using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Loomi.Tests;
public class TokenTests
{
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
    private static async Task<Guid> MemberAsync(AppFactory factory, string username, string password, int credits = 0)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new AppUser { Username = username, NormalizedUsername = username };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<PasswordService>().Hash(user, password);
        db.Users.Add(user);
        if (credits != 0) db.CreditEntries.Add(new CreditEntry { UserId = user.Id, Amount = credits, Kind = CreditKind.Grant });
        await db.SaveChangesAsync();
        return user.Id;
    }
    private static async Task<T> WithDbAsync<T>(AppFactory factory, Func<AppDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
    private static async Task<JsonElement> MintAsync(HttpClient session, string name = "cli")
    {
        var response = await session.PostAsJsonAsync("/api/tokens", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    /// <summary>A client that carries nothing but the token: no cookie jar, no CSRF header.</summary>
    private static HttpClient Bearer(AppFactory factory, string token)
    {
        var client = factory.CreateClient(new() { HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task A_bearer_token_works_without_a_cookie_or_a_csrf_header()
    {
        using var factory = new AppFactory();
        await MemberAsync(factory, "sara", "sara-strong-pass");
        using var session = await SignInAsync(factory, "sara", "sara-strong-pass");
        var minted = await MintAsync(session);
        using var api = Bearer(factory, minted.GetProperty("token").GetString()!);
        var me = await api.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.True(me.GetProperty("authenticated").GetBoolean());
        Assert.Equal("sara", me.GetProperty("username").GetString());
        var created = await api.PostAsJsonAsync("/api/projects", new { title = "from the api" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var project = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync($"/api/projects/{project.GetProperty("id").GetGuid()}")).StatusCode);
    }

    [Fact]
    public async Task The_token_is_shown_once_and_its_hash_never()
    {
        using var factory = new AppFactory();
        await MemberAsync(factory, "sara", "sara-strong-pass");
        using var session = await SignInAsync(factory, "sara", "sara-strong-pass");
        var minted = await MintAsync(session, "phone");
        var token = minted.GetProperty("token").GetString()!;
        Assert.StartsWith("lm_", token);
        Assert.Equal(44, token.Length);
        Assert.Equal(token.Substring(3, 8), minted.GetProperty("prefix").GetString());
        var listed = await session.GetFromJsonAsync<JsonElement>("/api/tokens");
        var row = Assert.Single(listed.EnumerateArray());
        Assert.Equal("phone", row.GetProperty("name").GetString());
        Assert.False(row.TryGetProperty("token", out _));
        Assert.False(row.TryGetProperty("hash", out _));
        var stored = await WithDbAsync(factory, db => db.ApiTokens.AsNoTracking().SingleAsync());
        Assert.NotEqual(token, stored.Hash);
        Assert.DoesNotContain(token.Substring(12), stored.Hash);
    }

    [Fact]
    public async Task A_revoked_token_is_refused()
    {
        using var factory = new AppFactory();
        await MemberAsync(factory, "sara", "sara-strong-pass");
        using var session = await SignInAsync(factory, "sara", "sara-strong-pass");
        var minted = await MintAsync(session);
        using var api = Bearer(factory, minted.GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await session.DeleteAsync($"/api/tokens/{minted.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/projects")).StatusCode);
        Assert.Empty((await session.GetFromJsonAsync<JsonElement>("/api/tokens")).EnumerateArray());
    }

    [Fact]
    public async Task A_disabled_users_token_is_refused()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory, "sara", "sara-strong-pass");
        using var session = await SignInAsync(factory, "sara", "sara-strong-pass");
        var minted = await MintAsync(session);
        using var api = Bearer(factory, minted.GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/projects")).StatusCode);
        await WithDbAsync(factory, db => db.Users.Where(u => u.Id == sara).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDisabled, true)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task A_cookie_session_still_needs_the_csrf_header()
    {
        using var factory = new AppFactory();
        await MemberAsync(factory, "sara", "sara-strong-pass");
        using var session = await SignInAsync(factory, "sara", "sara-strong-pass");
        session.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var response = await session.PostAsJsonAsync("/api/projects", new { title = "no csrf" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("InvalidCsrf", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_bearer_request_never_falls_back_to_the_cookie()
    {
        using var factory = new AppFactory();
        await MemberAsync(factory, "sara", "sara-strong-pass");
        using var session = await SignInAsync(factory, "sara", "sara-strong-pass");
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/projects")).StatusCode);
        // The cookie is valid and would be accepted on its own; the header decides which door the request goes through.
        session.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "lm_" + new string('a', 8) + "_" + new string('b', 32));
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.PostAsJsonAsync("/api/projects", new { title = "smuggled" })).StatusCode);
    }

    [Fact]
    public async Task Another_users_token_is_a_miss_not_a_refusal()
    {
        using var factory = new AppFactory();
        await MemberAsync(factory, "sara", "sara-strong-pass");
        await MemberAsync(factory, "omid", "omid-strong-pass");
        using var sara = await SignInAsync(factory, "sara", "sara-strong-pass");
        using var omid = await SignInAsync(factory, "omid", "omid-strong-pass");
        var minted = await MintAsync(sara);
        Assert.Equal(HttpStatusCode.NotFound, (await omid.DeleteAsync($"/api/tokens/{minted.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Empty((await omid.GetFromJsonAsync<JsonElement>("/api/tokens")).EnumerateArray());
        using var api = Bearer(factory, minted.GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task The_owner_sees_and_revokes_anyones_token()
    {
        using var factory = new AppFactory();
        var sara = await MemberAsync(factory, "sara", "sara-strong-pass");
        using var session = await SignInAsync(factory, "sara", "sara-strong-pass");
        using var owner = await SignInAsync(factory, "owner", AppFactory.Key);
        var minted = await MintAsync(session, "laptop");
        var listed = await owner.GetFromJsonAsync<JsonElement>($"/api/users/{sara}/tokens");
        var row = Assert.Single(listed.EnumerateArray());
        Assert.Equal("laptop", row.GetProperty("name").GetString());
        Assert.Equal(minted.GetProperty("prefix").GetString(), row.GetProperty("prefix").GetString());
        Assert.False(row.TryGetProperty("hash", out _));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/users/{sara}/tokens/{row.GetProperty("id").GetGuid()}")).StatusCode);
        using var api = Bearer(factory, minted.GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/projects")).StatusCode);
        // A member cannot reach the owner's view of another user's tokens at all.
        Assert.Equal(HttpStatusCode.Forbidden, (await session.GetAsync($"/api/users/{sara}/tokens")).StatusCode);
    }

    [Fact]
    public async Task A_token_is_limited_to_sixty_requests_a_minute()
    {
        using var factory = new AppFactory();
        await MemberAsync(factory, "sara", "sara-strong-pass");
        using var session = await SignInAsync(factory, "sara", "sara-strong-pass");
        var first = await MintAsync(session, "one");
        var second = await MintAsync(session, "two");
        using var api = Bearer(factory, first.GetProperty("token").GetString()!);
        for (var i = 0; i < 60; i++) Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/session")).StatusCode);
        var limited = await api.GetAsync("/api/session");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("RateLimited", (await limited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        // The window belongs to the token, not the user: a second token is not spent by the first, and neither is the cookie session.
        using var other = Bearer(factory, second.GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/session")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/session")).StatusCode);
    }

    [Fact]
    public async Task A_malformed_or_wrong_bearer_is_unauthorized_not_an_error()
    {
        using var factory = new AppFactory();
        await MemberAsync(factory, "sara", "sara-strong-pass");
        using var session = await SignInAsync(factory, "sara", "sara-strong-pass");
        var minted = await MintAsync(session);
        var token = minted.GetProperty("token").GetString()!;
        foreach (var bad in new[] { "nonsense", "", "lm_", token[..^1], token[..12] + new string('z', 32), token.ToUpperInvariant(), "lm_" + token.Substring(3, 8) + "_" + new string('!', 32) })
        {
            using var client = factory.CreateClient(new() { HandleCookies = false });
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + bad);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/projects")).StatusCode);
        }
        using var basic = factory.CreateClient(new() { HandleCookies = false });
        basic.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", "c2FyYTpzYXJh");
        Assert.Equal(HttpStatusCode.Unauthorized, (await basic.GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task A_token_opens_the_api_and_nothing_else()
    {
        using var factory = new AppFactory();
        await MemberAsync(factory, "sara", "sara-strong-pass");
        using var session = await SignInAsync(factory, "sara", "sara-strong-pass");
        var minted = await MintAsync(session);
        using var api = Bearer(factory, minted.GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/desktop/vnc.html")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.PostAsync("/hubs/status/negotiate?negotiateVersion=1", null)).StatusCode);
    }

    [Fact]
    public async Task Managing_tokens_takes_a_session_not_a_token()
    {
        using var factory = new AppFactory();
        await MemberAsync(factory, "sara", "sara-strong-pass");
        using var session = await SignInAsync(factory, "sara", "sara-strong-pass");
        var minted = await MintAsync(session);
        using var api = Bearer(factory, minted.GetProperty("token").GetString()!);
        var response = await api.PostAsJsonAsync("/api/tokens", new { name = "spawned" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("SessionRequired", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await api.GetAsync("/api/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.DeleteAsync($"/api/tokens/{minted.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Single((await session.GetFromJsonAsync<JsonElement>("/api/tokens")).EnumerateArray());
    }

    [Fact]
    public async Task Use_is_recorded_on_the_token_and_shown_in_the_list()
    {
        using var factory = new AppFactory();
        await MemberAsync(factory, "sara", "sara-strong-pass");
        using var session = await SignInAsync(factory, "sara", "sara-strong-pass");
        var minted = await MintAsync(session);
        var before = Assert.Single((await session.GetFromJsonAsync<JsonElement>("/api/tokens")).EnumerateArray());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("lastUsedAt").ValueKind);
        using var api = Bearer(factory, minted.GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/projects")).StatusCode);
        var after = Assert.Single((await session.GetFromJsonAsync<JsonElement>("/api/tokens")).EnumerateArray());
        Assert.Equal(JsonValueKind.String, after.GetProperty("lastUsedAt").ValueKind);
    }

    [Fact]
    public async Task A_bad_name_and_too_many_tokens_are_rejected()
    {
        using var factory = new AppFactory();
        await MemberAsync(factory, "sara", "sara-strong-pass");
        using var session = await SignInAsync(factory, "sara", "sara-strong-pass");
        foreach (var name in new object?[] { "", "   ", null, new string('n', 65) })
        {
            var response = await session.PostAsJsonAsync("/api/tokens", new { name });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("InvalidTokenName", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }
        for (var i = 0; i < 20; i++) await MintAsync(session, "t" + i);
        var overflow = await session.PostAsJsonAsync("/api/tokens", new { name = "one too many" });
        Assert.Equal(HttpStatusCode.Conflict, overflow.StatusCode);
        Assert.Equal("TooManyTokens", (await overflow.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        // Revoked tokens do not count against the ceiling.
        var listed = await session.GetFromJsonAsync<JsonElement>("/api/tokens");
        Assert.Equal(HttpStatusCode.NoContent, (await session.DeleteAsync($"/api/tokens/{listed[0].GetProperty("id").GetGuid()}")).StatusCode);
        await MintAsync(session, "room again");
    }

    [Fact]
    public async Task A_token_runs_the_whole_generation_flow_an_external_tool_needs()
    {
        using var factory = new AppFactory();
        await MemberAsync(factory, "sara", "sara-strong-pass", 100);
        using var session = await SignInAsync(factory, "sara", "sara-strong-pass");
        var minted = await MintAsync(session);
        using var api = Bearer(factory, minted.GetProperty("token").GetString()!);
        var project = await (await api.PostAsJsonAsync("/api/projects", new { title = "api flow" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = project.GetProperty("id").GetGuid();
        var queued = await api.PostAsJsonAsync($"/api/projects/{id}/generate", new { prompt = "a lighthouse at dusk" });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var generation = await queued.Content.ReadFromJsonAsync<JsonElement>();
        var gid = generation.GetProperty("id").GetGuid();
        Assert.Equal("Queued", (await api.GetFromJsonAsync<JsonElement>($"/api/generations/{gid}")).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync($"/api/generations/{gid}/image")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/generations/{gid}/cancel", null)).StatusCode);
        Assert.Equal("Cancelled", (await api.GetFromJsonAsync<JsonElement>($"/api/generations/{gid}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_openapi_document_is_open_in_development_and_lists_the_bearer_scheme()
    {
        using var factory = new AppFactory(); using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var paths = document.GetProperty("paths");
        foreach (var path in new[] { "/api/tokens", "/api/projects/{id}/generate", "/api/generations/{id}", "/api/generations/{id}/image" }) Assert.True(paths.TryGetProperty(path, out _), path);
        Assert.True(document.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("Bearer", out _));
    }

    [Fact]
    public async Task Outside_development_the_openapi_document_is_the_owners_alone()
    {
        using var factory = new UnconfiguredFactory("Production", AppFactory.Key);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/openapi/v1.json")).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var member = new AppUser { Username = "sara", NormalizedUsername = "sara" };
        member.PasswordHash = scope.ServiceProvider.GetRequiredService<PasswordService>().Hash(member, "sara-strong-pass");
        db.Users.Add(member); await db.SaveChangesAsync();
        // Production insists on HTTPS for the session cookie; the test server speaks it when asked.
        using var session = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var csrf = (await session.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("csrfToken").GetString();
        session.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.OK, (await session.PostAsJsonAsync("/api/session/login", new { username = "sara", password = "sara-strong-pass" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await session.GetAsync("/openapi/v1.json")).StatusCode);
    }
}
