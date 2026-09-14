using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Loomi.Tests;
public class UserAdminTests
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
    private static async Task<HttpClient> OwnerAsync(AppFactory factory) => await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
    private static async Task<AppUser> AddUserAsync(AppFactory factory, string username, string password, UserRole role = UserRole.Member)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new AppUser { Username = username, NormalizedUsername = username.ToLowerInvariant(), Role = role };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<PasswordService>().Hash(user, password);
        db.Users.Add(user); await db.SaveChangesAsync(); return user;
    }
    private static async Task<Guid> AddProjectAsync(AppFactory factory, Guid userId, string title, params (RunStatus Status, DateTime CreatedAt)[] generations)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IImageStorage>();
        var project = new ImageProject { UserId = userId, Title = title };
        db.Projects.Add(project);
        foreach (var (status, createdAt) in generations)
        {
            var generation = new Generation { ProjectId = project.Id, UserId = userId, Prompt = "Seeded", Status = status, CreatedAt = createdAt };
            if (status == RunStatus.Completed) generation.LocalImagePath = await storage.SaveAsync(project.Id, generation.Id, Convert.FromBase64String(Png), default);
            db.Generations.Add(generation);
        }
        await db.SaveChangesAsync(); return project.Id;
    }
    private static JsonElement Single(JsonElement users, string username) => users.EnumerateArray().Single(u => u.GetProperty("username").GetString() == username);

    [Fact]
    public async Task Users_are_listed_with_their_role_quota_and_what_they_have_spent_today()
    {
        using var factory = new AppFactory();
        var mia = await AddUserAsync(factory, "mia", "mia-password-1");
        // Failed work does not burn quota, and yesterday's work is not today's.
        await AddProjectAsync(factory, mia.Id, "Mia's project",
            (RunStatus.Completed, DateTime.UtcNow), (RunStatus.Queued, DateTime.UtcNow), (RunStatus.Failed, DateTime.UtcNow), (RunStatus.Completed, DateTime.UtcNow.AddDays(-2)));
        using var client = await OwnerAsync(factory);
        var users = await client.GetFromJsonAsync<JsonElement>("/api/users");
        Assert.Equal(2, users.GetArrayLength());
        var listed = Single(users, "mia");
        Assert.Equal("Member", listed.GetProperty("role").GetString());
        Assert.Equal(10, listed.GetProperty("dailyQuota").GetInt32());
        Assert.False(listed.GetProperty("isDisabled").GetBoolean());
        Assert.Equal(2, listed.GetProperty("usedToday").GetInt32());
        Assert.Equal(0, Single(users, OwnerBootstrap.Username).GetProperty("usedToday").GetInt32());
    }

    [Fact]
    public async Task A_member_is_refused_every_user_route()
    {
        using var factory = new AppFactory();
        var mia = await AddUserAsync(factory, "mia", "mia-password-1");
        using var client = await SignInAsync(factory, "mia", "mia-password-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/users", new { username = "sneak", password = "sneak-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync($"/api/users/{mia.Id}", new { dailyQuota = 9999 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/users/{mia.Id}/password", new { password = "taken-over-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/users/{mia.Id}")).StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(10, (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.SingleAsync(u => u.Id == mia.Id)).DailyQuota);
    }

    [Fact]
    public async Task User_routes_need_a_session_and_a_csrf_token()
    {
        using var factory = new AppFactory();
        using (var anonymous = factory.CreateClient())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/users")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/users", new { username = "sneak", password = "sneak-password" })).StatusCode);
        }
        using var client = await OwnerAsync(factory);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/users", new { username = "notoken", password = "no-token-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/users/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task A_new_user_is_created_normalized_and_can_sign_in_however_the_name_is_typed()
    {
        using var factory = new AppFactory();
        using var client = await OwnerAsync(factory);
        var created = await client.PostAsJsonAsync("/api/users", new { username = "Mia.Rossi", password = "mia-password-1", dailyQuota = 25 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var user = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Mia.Rossi", user.GetProperty("username").GetString());
        Assert.Equal(25, user.GetProperty("dailyQuota").GetInt32());
        Assert.Equal("Member", user.GetProperty("role").GetString());
        using (var scope = factory.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.SingleAsync(u => u.Username == "Mia.Rossi");
            Assert.Equal("mia.rossi", stored.NormalizedUsername);
        }
        using var mia = await SignInAsync(factory, "MIA.ROSSI", "mia-password-1");
        Assert.Equal("Mia.Rossi", (await mia.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("username").GetString());
        // The same name in another casing is the same name.
        var duplicate = await client.PostAsJsonAsync("/api/users", new { username = "mia.ROSSI", password = "another-password" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("DuplicateUsername", (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("ab", "long-enough-password", "InvalidUsername")]
    [InlineData("", "long-enough-password", "InvalidUsername")]
    [InlineData("mia rossi", "long-enough-password", "InvalidUsername")]
    [InlineData("mia@rossi", "long-enough-password", "InvalidUsername")]
    [InlineData("میا", "long-enough-password", "InvalidUsername")]
    [InlineData("mia", "short", "WeakPassword")]
    [InlineData("mia", "", "WeakPassword")]
    public async Task Creating_a_user_rejects_bad_input_with_a_stable_code(string username, string password, string error)
    {
        using var factory = new AppFactory();
        using var client = await OwnerAsync(factory);
        var response = await client.PostAsJsonAsync("/api/users", new { username, password });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.CountAsync());
    }

    [Fact]
    public async Task A_password_and_its_hash_never_come_back_out_of_the_api()
    {
        using var factory = new AppFactory();
        using var client = await OwnerAsync(factory);
        const string secret = "mia-password-1";
        var created = await client.PostAsJsonAsync("/api/users", new { username = "mia", password = secret });
        foreach (var payload in new[] { await created.Content.ReadAsStringAsync(), await client.GetStringAsync("/api/users") })
        {
            Assert.DoesNotContain(secret, payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("asswordHash", payload, StringComparison.Ordinal);
            Assert.DoesNotContain("assword", payload, StringComparison.Ordinal);
        }
        using var scope = factory.Services.CreateScope();
        Assert.DoesNotContain(secret, (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.SingleAsync(u => u.Username == "mia")).PasswordHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resetting_a_password_replaces_the_old_one()
    {
        using var factory = new AppFactory();
        var mia = await AddUserAsync(factory, "mia", "mia-password-1");
        using var client = await OwnerAsync(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/users/{mia.Id}/password", new { password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/users/{mia.Id}/password", new { password = "mia-password-2" })).StatusCode);
        using var stale = factory.CreateClient();
        stale.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await CsrfAsync(stale));
        Assert.Equal(HttpStatusCode.Unauthorized, (await stale.PostAsJsonAsync("/api/session/login", new { username = "mia", password = "mia-password-1" })).StatusCode);
        using var fresh = await SignInAsync(factory, "mia", "mia-password-2");
        Assert.True((await fresh.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task The_owner_cannot_lock_the_workspace_by_removing_the_last_owner()
    {
        using var factory = new AppFactory();
        using var client = await OwnerAsync(factory);
        var owner = (await client.GetFromJsonAsync<JsonElement>("/api/users")).EnumerateArray().Single().GetProperty("id").GetGuid();
        foreach (var (body, error) in new[]
        {
            ((object)new { isDisabled = true }, "CannotDisableSelf"),
            (new { role = "Member" }, "LastOwner"),
        })
        {
            var response = await client.PatchAsJsonAsync($"/api/users/{owner}", body);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(error, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }
        var deleted = await client.DeleteAsync($"/api/users/{owner}");
        Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.Equal("CannotDeleteSelf", (await deleted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        // With a second owner in place the same demotion is allowed, because somebody is still holding the keys.
        var second = await AddUserAsync(factory, "nina", "nina-password-1", UserRole.Owner);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/users/{owner}", new { role = "Member" })).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(UserRole.Member, (await db.Users.SingleAsync(u => u.Id == owner)).Role);
        Assert.Equal(UserRole.Owner, (await db.Users.SingleAsync(u => u.Id == second.Id)).Role);
    }

    [Fact]
    public async Task A_disabled_owner_does_not_count_as_the_owner_still_holding_the_keys()
    {
        using var factory = new AppFactory();
        var sleeping = await AddUserAsync(factory, "nina", "nina-password-1", UserRole.Owner);
        using var client = await OwnerAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/users/{sleeping.Id}", new { isDisabled = true })).StatusCode);
        var owner = Single(await client.GetFromJsonAsync<JsonElement>("/api/users"), OwnerBootstrap.Username).GetProperty("id").GetGuid();
        var response = await client.PatchAsJsonAsync($"/api/users/{owner}", new { role = "Member" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("LastOwner", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Quota_role_and_the_disabled_flag_are_updated_together()
    {
        using var factory = new AppFactory();
        var mia = await AddUserAsync(factory, "mia", "mia-password-1");
        using var client = await OwnerAsync(factory);
        var response = await client.PatchAsJsonAsync($"/api/users/{mia.Id}", new { dailyQuota = 40, role = "Owner", isDisabled = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(40, updated.GetProperty("dailyQuota").GetInt32());
        Assert.Equal("Owner", updated.GetProperty("role").GetString());
        Assert.True(updated.GetProperty("isDisabled").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync($"/api/users/{mia.Id}", new { dailyQuota = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"/api/users/{Guid.NewGuid()}", new { dailyQuota = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/users/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/users/{Guid.NewGuid()}/password", new { password = "long-enough-password" })).StatusCode);
    }

    [Fact]
    public async Task Disabling_a_signed_in_user_ends_their_access_without_waiting_for_the_cookie_to_expire()
    {
        using var factory = new AppFactory();
        var mia = await AddUserAsync(factory, "mia", "mia-password-1");
        using var member = await SignInAsync(factory, "mia", "mia-password-1");
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/projects")).StatusCode);
        using var client = await OwnerAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/users/{mia.Id}", new { isDisabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/api/projects")).StatusCode);
        Assert.False((await member.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task Deleting_a_signed_in_user_ends_their_access_immediately()
    {
        using var factory = new AppFactory();
        var mia = await AddUserAsync(factory, "mia", "mia-password-1");
        using var member = await SignInAsync(factory, "mia", "mia-password-1");
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/projects")).StatusCode);
        using var client = await OwnerAsync(factory);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/users/{mia.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task A_demoted_owner_loses_the_owner_only_routes_without_signing_in_again()
    {
        using var factory = new AppFactory();
        var nina = await AddUserAsync(factory, "nina", "nina-password-1", UserRole.Owner);
        using var demoted = await SignInAsync(factory, "nina", "nina-password-1");
        Assert.Equal(HttpStatusCode.OK, (await demoted.GetAsync("/api/users")).StatusCode);
        using var client = await OwnerAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/users/{nina.Id}", new { role = "Member" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await demoted.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_user_takes_their_own_work_and_leaves_everybody_elses_standing()
    {
        using var factory = new AppFactory();
        var mia = await AddUserAsync(factory, "mia", "mia-password-1");
        var max = await AddUserAsync(factory, "max", "max-password-1");
        var miaProject = await AddProjectAsync(factory, mia.Id, "Mia's project", (RunStatus.Completed, DateTime.UtcNow), (RunStatus.Completed, DateTime.UtcNow));
        var maxProject = await AddProjectAsync(factory, max.Id, "Max's project", (RunStatus.Completed, DateTime.UtcNow));
        Guid strayId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Lineage inside the doomed project: ParentGenerationId is Restrict, so a cascade that ignores it fails outright.
            var own = await db.Generations.Where(g => g.ProjectId == miaProject).OrderBy(g => g.CreatedAt).ToListAsync();
            own[1].ParentGenerationId = own[0].Id;
            // A row of Mia's sitting inside Max's project, and built on one of hers: deleting it would punch a hole in Max's history,
            // and leaving the reference to the doomed parent behind would fail the Restrict constraint outright.
            var stray = new Generation { ProjectId = maxProject, UserId = mia.Id, Prompt = "Mia lent a hand", Status = RunStatus.Completed, ParentGenerationId = own[0].Id };
            strayId = stray.Id;
            db.Generations.Add(stray); await db.SaveChangesAsync();
        }
        using var client = await OwnerAsync(factory);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/users/{mia.Id}")).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Users.AnyAsync(u => u.Id == mia.Id));
            Assert.False(await db.Projects.AnyAsync(p => p.Id == miaProject));
            Assert.False(await db.Generations.AnyAsync(g => g.ProjectId == miaProject));
            Assert.True(await db.Projects.AnyAsync(p => p.Id == maxProject));
            Assert.Equal(2, await db.Generations.CountAsync(g => g.ProjectId == maxProject));
            // The stray survives and is re-homed to the project's owner, so quota counting still adds up.
            var stray = await db.Generations.SingleAsync(g => g.Id == strayId);
            Assert.Equal(max.Id, stray.UserId);
            Assert.Null(stray.ParentGenerationId);
        }
        var storage = factory.Services.GetRequiredService<IImageStorage>();
        Assert.False(Directory.Exists(storage.Resolve($"projects/{miaProject}")));
        Assert.True(Directory.Exists(storage.Resolve($"projects/{maxProject}")));
    }

    [Fact]
    public async Task A_user_with_work_still_running_is_not_deleted_out_from_under_it()
    {
        using var factory = new AppFactory();
        var mia = await AddUserAsync(factory, "mia", "mia-password-1");
        var project = await AddProjectAsync(factory, mia.Id, "Mia's project", (RunStatus.GeneratingImage, DateTime.UtcNow));
        using var client = await OwnerAsync(factory);
        var response = await client.DeleteAsync($"/api/users/{mia.Id}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("UserBusy", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Generations.Where(g => g.ProjectId == project).ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, RunStatus.Failed));
        }
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/users/{mia.Id}")).StatusCode);
    }
}
