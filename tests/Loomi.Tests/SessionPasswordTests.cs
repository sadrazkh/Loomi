using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Loomi.Security;
using Xunit;

namespace Loomi.Tests;
/// <summary>Self-service password change: a signed-in user proves they still know the old password before a new one takes over.</summary>
public class SessionPasswordTests
{
    private static async Task<string> CsrfAsync(HttpClient client) => (await client.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("csrfToken").GetString()!;
    private static async Task<HttpClient> SignInAsync(AppFactory factory, string username, string password)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await CsrfAsync(client));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/session/login", new { username, password })).StatusCode);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await CsrfAsync(client));
        return client;
    }
    private static async Task<JsonElement> MintAsync(HttpClient session)
    {
        var response = await session.PostAsJsonAsync("/api/tokens", new { name = "cli" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task The_wrong_current_password_is_refused()
    {
        using var factory = new AppFactory();
        using var session = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        var response = await session.PostAsJsonAsync("/api/session/password", new { currentPassword = "not-the-real-one", newPassword = "a-brand-new-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("InvalidCurrentPassword", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        // The old password still works: nothing was changed.
        using var stillOwner = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        Assert.Equal(HttpStatusCode.OK, (await stillOwner.GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task A_weak_new_password_is_refused()
    {
        using var factory = new AppFactory();
        using var session = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        var response = await session.PostAsJsonAsync("/api/session/password", new { currentPassword = AppFactory.Key, newPassword = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("WeakPassword", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Success_lets_the_new_password_sign_in_and_the_old_one_not()
    {
        using var factory = new AppFactory();
        using var session = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        Assert.Equal(HttpStatusCode.NoContent, (await session.PostAsJsonAsync("/api/session/password", new { currentPassword = AppFactory.Key, newPassword = "a-brand-new-password" })).StatusCode);
        // The existing session cookie still works until it expires; a fresh login is what proves the password actually changed.
        using var oldLogin = factory.CreateClient();
        oldLogin.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await CsrfAsync(oldLogin));
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldLogin.PostAsJsonAsync("/api/session/login", new { username = OwnerBootstrap.Username, password = AppFactory.Key })).StatusCode);
        using var newLogin = await SignInAsync(factory, OwnerBootstrap.Username, "a-brand-new-password");
        Assert.Equal(HttpStatusCode.OK, (await newLogin.GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task A_bearer_token_cannot_change_the_password()
    {
        using var factory = new AppFactory();
        using var session = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        var minted = await MintAsync(session);
        using var api = factory.CreateClient(new() { HandleCookies = false });
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", minted.GetProperty("token").GetString()!);
        var response = await api.PostAsJsonAsync("/api/session/password", new { currentPassword = AppFactory.Key, newPassword = "a-brand-new-password" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("SessionRequired", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        // The old password still works: nothing was changed.
        using var stillOwner = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        Assert.Equal(HttpStatusCode.OK, (await stillOwner.GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await CsrfAsync(client));
        var response = await client.PostAsJsonAsync("/api/session/password", new { currentPassword = "x", newPassword = "a-brand-new-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
