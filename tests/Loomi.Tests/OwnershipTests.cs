using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Loomi.Tests;
/// <summary>Serves the ChatGPT stand-in, so creating a project over HTTP does not need a real login.</summary>
public class FixtureBrowserFactory : AppFactory
{
    public FixtureLauncher Launcher { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => { services.RemoveAll<IChromiumLauncher>(); services.AddSingleton<IChromiumLauncher>(Launcher); });
    }
}
public class OwnershipTests
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nS8AAAAASUVORK5CYII=";
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
    private static async Task<AppUser> AddUserAsync(AppFactory factory, string username, string password, UserRole role = UserRole.Member, bool disabled = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new AppUser { Username = username, NormalizedUsername = username.ToLowerInvariant(), Role = role, IsDisabled = disabled };
        db.CreditEntries.Add(new CreditEntry { UserId = user.Id, Amount = 1000, Kind = CreditKind.Grant });
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<PasswordService>().Hash(user, password);
        db.Users.Add(user); await db.SaveChangesAsync(); return user;
    }
    private static async Task<(Guid Project, Guid Generation)> AddWorkAsync(AppFactory factory, Guid userId, string title)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var project = new ImageProject { UserId = userId, Title = title };
        var generation = new Generation { ProjectId = project.Id, UserId = userId, Prompt = "Seeded", Status = RunStatus.Completed };
        generation.LocalImagePath = await scope.ServiceProvider.GetRequiredService<IImageStorage>().SaveAsync(project.Id, generation.Id, Convert.FromBase64String(Png), default);
        db.Projects.Add(project); db.Generations.Add(generation); await db.SaveChangesAsync();
        return (project.Id, generation.Id);
    }

    [Fact]
    public async Task Bootstrap_creates_an_owner_that_signs_in_with_the_access_key()
    {
        using var factory = new AppFactory();
        using var client = await SignInAsync(factory, "OWNER", AppFactory.Key);   // the name is matched however it was typed
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.True(session.GetProperty("authenticated").GetBoolean());
        Assert.Equal("owner", session.GetProperty("username").GetString());
        Assert.Equal("Owner", session.GetProperty("role").GetString());
        // Zero is what says no daily limit; the owner has none.
        Assert.Equal(0, session.GetProperty("dailyQuota").GetInt32());
        using var scope = factory.Services.CreateScope();
        var owner = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.SingleAsync();
        Assert.DoesNotContain(AppFactory.Key, owner.PasswordHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_refuses_a_wrong_password_an_unknown_name_and_a_disabled_account()
    {
        using var factory = new AppFactory();
        await AddUserAsync(factory, "dan", "dan-password", disabled: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await CsrfAsync(client));
        foreach (var (username, password, error) in new[] { ("owner", "not-the-key", "InvalidCredentials"), ("nobody", AppFactory.Key, "InvalidCredentials"), ("dan", "dan-password", "AccountDisabled") })
        {
            var response = await client.PostAsJsonAsync("/api/session/login", new { username, password });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(error, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task A_member_cannot_read_or_change_another_members_work()
    {
        using var factory = new AppFactory();
        var mia = await AddUserAsync(factory, "mia", "mia-password");
        await AddUserAsync(factory, "max", "max-password");
        var work = await AddWorkAsync(factory, mia.Id, "Mia's private project");
        using var client = await SignInAsync(factory, "max", "max-password");
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/projects")).EnumerateArray());
        foreach (var path in new[] { $"/api/projects/{work.Project}", $"/api/projects/{work.Project}/generations", $"/api/generations/{work.Generation}", $"/api/generations/{work.Generation}/image" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        foreach (var path in new[] { $"/api/projects/{work.Project}/generate", $"/api/generations/{work.Generation}/edit", $"/api/generations/{work.Generation}/branch" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(path, new { prompt = "Take this over" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/projects/{work.Project}")).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Projects.AnyAsync(p => p.Id == work.Project));
        Assert.Equal(1, await db.Generations.CountAsync(g => g.ProjectId == work.Project));
    }

    [Fact]
    public async Task An_owner_reaches_every_members_work_and_quota_follows_the_project()
    {
        using var factory = new AppFactory();
        var mia = await AddUserAsync(factory, "mia", "mia-password");
        var work = await AddWorkAsync(factory, mia.Id, "Mia's project");
        using var client = await SignInAsync(factory, "owner", AppFactory.Key);
        Assert.Equal(work.Project, (await client.GetFromJsonAsync<JsonElement>("/api/projects")).EnumerateArray().Single().GetProperty("id").GetGuid());
        foreach (var path in new[] { $"/api/projects/{work.Project}", $"/api/projects/{work.Project}/generations", $"/api/generations/{work.Generation}", $"/api/generations/{work.Generation}/image" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/projects/{work.Project}/generate", new { prompt = "Owner lends a hand" })).StatusCode);
        using var scope = factory.Services.CreateScope();
        var queued = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Generations.SingleAsync(g => g.Status == RunStatus.Queued);
        Assert.Equal(mia.Id, queued.UserId);
    }

    [Fact]
    public async Task A_new_project_and_its_generations_record_the_signed_in_user()
    {
        await using var factory = new FixtureBrowserFactory();
        await using var launcher = factory.Launcher;
        var mia = await AddUserAsync(factory, "mia", "mia-password");
        using var client = await SignInAsync(factory, "mia", "mia-password");
        var created = await client.PostAsJsonAsync("/api/projects", new { title = "Mia's first" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var projectId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/projects/{projectId}/generate", new { prompt = "A quiet harbour at dawn" })).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(mia.Id, (await db.Projects.SingleAsync()).UserId);
        Assert.Equal(mia.Id, (await db.Generations.SingleAsync()).UserId);
    }
}
