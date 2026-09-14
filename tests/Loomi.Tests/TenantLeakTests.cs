using System.Net.Http.Json;
using System.Text.Json;
using Loomi.Data;
using Loomi.Models;
using Loomi.Security;
using Loomi.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Loomi.Tests;
/// <summary>Records who a status event was addressed to. Every audience but User is a leak here, so they all fail loudly.</summary>
public class RecordingHub : IHubContext<StatusHub>, IHubClients, IClientProxy
{
    public List<string> Addressed { get; } = [];
    public bool Broadcast { get; private set; }
    public IHubClients Clients => this;
    public IGroupManager Groups => throw new NotSupportedException();
    public IClientProxy All { get { Broadcast = true; return this; } }
    public IClientProxy User(string userId) { Addressed.Add(userId); return this; }
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public IClientProxy AllExcept(IReadOnlyList<string> excluded) { Broadcast = true; return this; }
    public IClientProxy Client(string connectionId) => throw new NotSupportedException();
    IClientProxy IHubClients<IClientProxy>.Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
    public IClientProxy Group(string groupName) => throw new NotSupportedException();
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excluded) => throw new NotSupportedException();
    IClientProxy IHubClients<IClientProxy>.Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
    public IClientProxy Users(IReadOnlyList<string> userIds) { Addressed.AddRange(userIds); return this; }
}
public class TenantLeakTests
{
    [Fact]
    public async Task The_development_key_is_only_offered_to_someone_who_has_not_signed_in()
    {
        using var factory = new UnconfiguredFactory("Development", null); using var client = factory.CreateClient();
        var anonymous = await client.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Equal(DevelopmentAccess.Key, anonymous.GetProperty("devAccessKey").GetString());
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", anonymous.GetProperty("csrfToken").GetString());
        await client.PostAsJsonAsync("/api/session/login", new { username = OwnerBootstrap.Username, password = DevelopmentAccess.Key });
        // Signed in it is nothing but the owner's password, and a member would be reading it too.
        Assert.Equal(JsonValueKind.Null, (await client.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("devAccessKey").ValueKind);
    }

    [Fact]
    public async Task A_status_event_is_addressed_to_the_owner_of_the_work_and_not_broadcast()
    {
        using var factory = new AppFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();
            var mia = new AppUser { Username = "mia", NormalizedUsername = "mia" };
            mia.PasswordHash = passwords.Hash(mia, "mia-strong-pass");
            db.Users.Add(mia);
            var project = new ImageProject { UserId = mia.Id, Title = "Mia's work" };
            db.Projects.Add(project);
            db.Generations.Add(new Generation { ProjectId = project.Id, UserId = mia.Id, Prompt = "a private prompt" });
            await db.SaveChangesAsync();
        }
        var hub = new RecordingHub();
        await GenerationWorker.NotifyAsync(hub, await OnlyGenerationAsync(factory), default);
        Assert.Equal("mia", await UsernameOfAsync(factory, Assert.Single(hub.Addressed)));
        Assert.False(hub.Broadcast, "the event went to every connected browser");
    }

    private static async Task<Generation> OnlyGenerationAsync(AppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Generations.Include(g => g.Project).SingleAsync();
    }

    private static async Task<string> UsernameOfAsync(AppFactory factory, string id)
    {
        using var scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.SingleAsync(u => u.Id == Guid.Parse(id))).Username;
    }
}
