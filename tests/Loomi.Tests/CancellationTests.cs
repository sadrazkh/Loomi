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
public class CancellationTests
{
    private static async Task<HttpClient> SignInAsync(AppFactory factory, string username, string password)
    {
        var client = factory.CreateClient();
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        var login = await client.PostAsJsonAsync("/api/session/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        return client;
    }

    private static async Task<(Guid Project, Guid Generation)> QueueAsync(AppFactory factory, Guid user, string prompt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var project = new ImageProject { UserId = user, Title = prompt, Status = "Queued" };
        db.Projects.Add(project);
        var g = new Generation { ProjectId = project.Id, UserId = user, Prompt = prompt };
        db.Generations.Add(g);
        await db.SaveChangesAsync();
        return (project.Id, g.Id);
    }

    private static async Task<Guid> AddMemberAsync(AppFactory factory, string username, string password)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new AppUser { Username = username, NormalizedUsername = username };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<PasswordService>().Hash(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<string> StatusOfAsync(AppFactory factory, Guid generation)
    {
        using var scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Generations.AsNoTracking().FirstAsync(g => g.Id == generation)).Status.ToString();
    }

    [Fact]
    public async Task A_member_cancels_their_own_queued_work_and_cannot_touch_anyone_elses()
    {
        using var factory = new AppFactory();
        var mia = await AddMemberAsync(factory, "mia", "mia-strong-pass");
        var max = await AddMemberAsync(factory, "max", "max-strong-pass");
        var hers = await QueueAsync(factory, mia, "mia waiting");
        var his = await QueueAsync(factory, max, "max waiting");
        using var client = await SignInAsync(factory, "mia", "mia-strong-pass");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/generations/{hers.Generation}/cancel", null)).StatusCode);
        Assert.Equal("Cancelled", await StatusOfAsync(factory, hers.Generation));
        // Another member's id is a miss, not a refusal, so ids cannot be probed.
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/generations/{his.Generation}/cancel", null)).StatusCode);
        Assert.Equal("Queued", await StatusOfAsync(factory, his.Generation));
    }

    [Fact]
    public async Task Cancelling_everything_reaches_only_the_callers_own_queue()
    {
        using var factory = new AppFactory();
        var mia = await AddMemberAsync(factory, "mia", "mia-strong-pass");
        var max = await AddMemberAsync(factory, "max", "max-strong-pass");
        var first = await QueueAsync(factory, mia, "one");
        var second = await QueueAsync(factory, mia, "two");
        var his = await QueueAsync(factory, max, "his");
        using var client = await SignInAsync(factory, "mia", "mia-strong-pass");
        var response = await client.PostAsync("/api/generations/cancel", null);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cancelled").GetInt32());
        Assert.Equal("Cancelled", await StatusOfAsync(factory, first.Generation));
        Assert.Equal("Cancelled", await StatusOfAsync(factory, second.Generation));
        Assert.Equal("Queued", await StatusOfAsync(factory, his.Generation));
    }

    [Fact]
    public async Task The_owner_can_clear_the_whole_queue()
    {
        using var factory = new AppFactory();
        var mia = await AddMemberAsync(factory, "mia", "mia-strong-pass");
        var max = await AddMemberAsync(factory, "max", "max-strong-pass");
        var hers = await QueueAsync(factory, mia, "hers");
        var his = await QueueAsync(factory, max, "his");
        using var client = await SignInAsync(factory, OwnerBootstrap.Username, AppFactory.Key);
        var response = await client.PostAsync("/api/generations/cancel", null);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cancelled").GetInt32());
        Assert.Equal("Cancelled", await StatusOfAsync(factory, hers.Generation));
        Assert.Equal("Cancelled", await StatusOfAsync(factory, his.Generation));
    }

    [Fact]
    public async Task Cancelled_work_costs_the_user_nothing_and_finished_work_cannot_be_cancelled()
    {
        using var factory = new AppFactory();
        var mia = await AddMemberAsync(factory, "mia", "mia-strong-pass");
        var queued = await QueueAsync(factory, mia, "counts until cancelled");
        using var client = await SignInAsync(factory, "mia", "mia-strong-pass");
        await client.PostAsync($"/api/generations/{queued.Generation}/cancel", null);
        using (var scope = factory.Services.CreateScope())
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().UsedByAsync(mia, default));
        // Cancelling something already settled is a miss: there is nothing left to stop.
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/generations/{queued.Generation}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task A_member_cannot_manage_the_workspaces_ChatGPT_accounts()
    {
        using var factory = new AppFactory();
        await AddMemberAsync(factory, "mia", "mia-strong-pass");
        using var client = await SignInAsync(factory, "mia", "mia-strong-pass");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/auth/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/auth/accounts", new { label = "Theirs" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/auth/accounts/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/auth/accounts/{Guid.NewGuid()}/connect", null)).StatusCode);
        // Seeing whether the workspace is connected is not managing it.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/status")).StatusCode);
    }
}
