using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Loomi.Tests;
public class AccountApiTests
{
    private static async Task<HttpClient> Authenticated(AppFactory factory)
    {
        var client = factory.CreateClient();
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        await client.PostAsJsonAsync("/api/session/login", new { accessKey = AppFactory.Key });
        session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        return client;
    }

    [Fact]
    public async Task Accounts_are_listed_created_adjusted_and_deleted()
    {
        using var factory = new AppFactory();
        using var client = await Authenticated(factory);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/auth/accounts")).GetArrayLength());
        var created = await client.PostAsJsonAsync("/api/auth/accounts", new { label = "Studio account" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var account = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = account.GetProperty("id").GetGuid();
        Assert.Equal("Studio account", account.GetProperty("label").GetString());
        Assert.Equal("Disconnected", account.GetProperty("state").GetString());
        Assert.False(account.GetProperty("busy").GetBoolean());
        Assert.True(account.GetProperty("isEnabled").GetBoolean());
        var listed = await client.GetStringAsync("/api/auth/accounts");
        Assert.DoesNotContain("rofileDirectory", listed);
        Assert.DoesNotContain(factory.Root, listed);
        using (var scope = factory.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Accounts.SingleAsync();
            Assert.DoesNotContain(' ', stored.ProfileDirectory);
            Assert.Equal(stored.ProfileDirectory, Path.GetFileName(BrowserSession.ProfilePath(factory.Root, stored.ProfileDirectory)));
        }
        var status = await client.GetFromJsonAsync<JsonElement>($"/api/auth/accounts/{id}/status");
        Assert.Equal("Disconnected", status.GetProperty("state").GetString());
        Assert.False(status.GetProperty("busy").GetBoolean());
        var patched = await client.PatchAsJsonAsync($"/api/auth/accounts/{id}", new { dailyCap = 4, isEnabled = false });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var updated = await patched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, updated.GetProperty("dailyCap").GetInt32());
        Assert.False(updated.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/auth/accounts/{id}")).StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/auth/accounts")).GetArrayLength());
        // Polling the pre-pool status route must not quietly add an account back.
        Assert.Equal("Disconnected", (await client.GetFromJsonAsync<JsonElement>("/api/auth/status")).GetProperty("state").GetString());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/auth/accounts")).GetArrayLength());
    }

    [Fact]
    public async Task An_unknown_account_is_not_found_before_any_browser_starts()
    {
        using var factory = new AppFactory();
        using var client = await Authenticated(factory);
        var missing = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/auth/accounts/{missing}/status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/auth/accounts/{missing}/connect", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/auth/accounts/{missing}/reset", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"/api/auth/accounts/{missing}", new { dailyCap = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/accounts", new { label = "" })).StatusCode);
    }

    [Fact]
    public async Task Account_routes_need_a_session_and_a_csrf_token()
    {
        using var factory = new AppFactory();
        using (var anonymous = factory.CreateClient())
            foreach (var path in new[] { "/api/auth/accounts", $"/api/auth/accounts/{Guid.NewGuid()}/status" })
                Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        using var client = await Authenticated(factory);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/accounts", new { label = "No token" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/auth/accounts/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task The_single_browser_callers_adopt_the_existing_profile_and_refuse_a_disabled_account()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var adopted = await BrowserAutomationService.AccountAsync(db, default);
        Assert.Equal("default", adopted.ProfileDirectory);
        Assert.Equal(adopted.Id, (await BrowserAutomationService.AccountAsync(db, default)).Id);
        adopted.IsEnabled = false;
        await db.SaveChangesAsync();
        Assert.Equal("NoAccount", (await Assert.ThrowsAsync<InvalidOperationException>(() => BrowserAutomationService.AccountAsync(db, default))).Message);
        db.Accounts.Add(new BrowserAccount { Label = "Second", ProfileDirectory = "second" });
        await db.SaveChangesAsync();
        Assert.Equal("second", (await BrowserAutomationService.AccountAsync(db, default)).ProfileDirectory);
    }
}
