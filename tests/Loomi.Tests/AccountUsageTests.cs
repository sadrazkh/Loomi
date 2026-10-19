using System.Net.Http.Json;
using System.Text.Json;
using Loomi.Models;
using Loomi.Services;
using Xunit;

namespace Loomi.Tests;
/// <summary>The owner sees how much of each ChatGPT account's daily cap is gone, counted the way the dispatcher counts it.</summary>
public class AccountUsageTests
{
    [Fact]
    public async Task Each_account_says_how_many_of_todays_images_it_has_made()
    {
        using var world = await ApiV1World.CreateAsync();
        var (sara, _) = await world.MemberAsync("sara");
        var (_, owner) = await world.OwnerAsync();
        await world.DbAsync(async db =>
        {
            var project = new ImageProject { UserId = sara, Title = "p" };
            db.Projects.Add(project);
            foreach (var (status, at) in new[] { (RunStatus.Completed, Quota.Midnight().AddHours(1)), (RunStatus.Completed, Quota.Midnight().AddHours(2)),
                                                 (RunStatus.Failed, Quota.Midnight().AddHours(3)), (RunStatus.Completed, Quota.Midnight().AddHours(-2)) })
                db.Generations.Add(new Generation { ProjectId = project.Id, UserId = sara, AccountId = world.Account, Prompt = "x", Status = status, CreatedAt = at });
            return await db.SaveChangesAsync();
        });
        var accounts = await owner.GetFromJsonAsync<JsonElement>("/api/auth/accounts");
        Assert.Equal(2, accounts[0].GetProperty("usedToday").GetInt32());
    }
}
