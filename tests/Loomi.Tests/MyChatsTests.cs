using System.Net.Http.Json;
using System.Text.Json;
using Loomi.Models;
using Xunit;

namespace Loomi.Tests;
/// <summary>The owner may open anybody's work, but the chat list beside their own composer is theirs alone: other people's chats belong in management.</summary>
public class MyChatsTests
{
    private static async Task<Guid> ChatAsync(ApiV1World world, Guid user, string title) => await world.DbAsync(async db =>
    {
        var project = new ImageProject { UserId = user, Title = title };
        db.Projects.Add(project); await db.SaveChangesAsync();
        return project.Id;
    });

    [Fact]
    public async Task Asked_for_their_own_the_owner_gets_only_their_own_chats()
    {
        using var world = await ApiV1World.CreateAsync();
        var (sara, _) = await world.MemberAsync("sara");
        var (owner, client) = await world.OwnerAsync();
        await ChatAsync(world, sara, "sara's chat");
        await ChatAsync(world, owner, "owner's chat");
        var all = await client.GetFromJsonAsync<JsonElement>("/api/projects");
        Assert.Equal(2, all.GetArrayLength());
        var mine = await client.GetFromJsonAsync<JsonElement>("/api/projects?mine=true");
        Assert.Equal(["owner's chat"], mine.EnumerateArray().Select(p => p.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task A_member_sees_the_same_either_way()
    {
        using var world = await ApiV1World.CreateAsync();
        var (sara, client) = await world.MemberAsync("sara");
        var (owner, _) = await world.OwnerAsync();
        await ChatAsync(world, sara, "sara's chat");
        await ChatAsync(world, owner, "owner's chat");
        foreach (var path in new[] { "/api/projects", "/api/projects?mine=true" })
            Assert.Equal(["sara's chat"], (await client.GetFromJsonAsync<JsonElement>(path)).EnumerateArray().Select(p => p.GetProperty("title").GetString()));
    }
}
