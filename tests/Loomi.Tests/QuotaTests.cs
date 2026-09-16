using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Loomi.Tests;
public class QuotaTests
{
    private static async Task<HttpClient> SignInAsync(AppFactory factory, string username, string password)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", (await client.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("csrfToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/session/login", new { username, password })).StatusCode);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", (await client.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("csrfToken").GetString());
        return client;
    }
    /// <summary>A member with a project of their own, so a refusal can only have come from the quota.</summary>
    private static async Task<(Guid User, Guid Project)> MemberAsync(AppFactory factory, int quota, int today, RunStatus status = RunStatus.Completed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new AppUser { Username = "mia", NormalizedUsername = "mia", DailyQuota = quota };
        db.CreditEntries.Add(new CreditEntry { UserId = user.Id, Amount = 1000, Kind = CreditKind.Grant });
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<PasswordService>().Hash(user, "mia-password");
        var project = new ImageProject { UserId = user.Id, Title = "Quota" };
        db.Users.Add(user); db.Projects.Add(project);
        for (var i = 0; i < today; i++) db.Generations.Add(new Generation { ProjectId = project.Id, UserId = user.Id, Prompt = $"Earlier {i}", Status = status });
        await db.SaveChangesAsync();
        return (user.Id, project.Id);
    }

    [Fact]
    public async Task A_user_who_has_spent_their_day_is_refused_the_moment_they_queue_more()
    {
        using var factory = new AppFactory();
        var mia = await MemberAsync(factory, quota: 2, today: 2);
        using var client = await SignInAsync(factory, "mia", "mia-password");
        var refused = await client.PostAsJsonAsync($"/api/projects/{mia.Project}/generate", new { prompt = "One more" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("QuotaExceeded", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Equal(2, session.GetProperty("dailyUsed").GetInt32());
        Assert.Equal(0, session.GetProperty("dailyRemaining").GetInt32());
        using var scope = factory.Services.CreateScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Generations.CountAsync());
    }

    [Fact]
    public async Task A_failed_run_and_yesterdays_work_leave_the_day_unspent()
    {
        using var factory = new AppFactory();
        var mia = await MemberAsync(factory, quota: 2, today: 2, status: RunStatus.Failed);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Generations.Add(new Generation { ProjectId = mia.Project, UserId = mia.User, Prompt = "Yesterday", Status = RunStatus.Completed, CreatedAt = DateTime.UtcNow.AddDays(-1) });
            await db.SaveChangesAsync();
        }
        using var client = await SignInAsync(factory, "mia", "mia-password");
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/projects/{mia.Project}/generate", new { prompt = "Still allowed" })).StatusCode);
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Equal(1, session.GetProperty("dailyUsed").GetInt32());
        Assert.Equal(1, session.GetProperty("dailyRemaining").GetInt32());
    }

    [Fact]
    public async Task The_quota_spent_is_the_project_owners_even_when_the_workspace_owner_queues_the_work()
    {
        using var factory = new AppFactory();
        var mia = await MemberAsync(factory, quota: 1, today: 1);
        using var client = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        var refused = await client.PostAsJsonAsync($"/api/projects/{mia.Project}/generate", new { prompt = "Owner lends a hand" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("QuotaExceeded", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        // The member's spent day says nothing about the owner's own allowance.
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Equal(0, session.GetProperty("dailyUsed").GetInt32());
        // No daily limit is reported as nothing left to report, not as a number close to infinity.
        Assert.Equal(JsonValueKind.Null, session.GetProperty("dailyRemaining").ValueKind);
    }
}
