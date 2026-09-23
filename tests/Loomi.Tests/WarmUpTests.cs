using Loomi.BrowserAutomation;
using Loomi.Data;
using Loomi.Models;
using Loomi.Providers;
using Loomi.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Loomi.Tests;
/// <summary>A deployment restarts on every update, and after a restart every browser account read as disconnected until the owner pressed Connect, with
/// the UI telling everyone else to wait for that. The accounts are brought up when the server starts instead.</summary>
public class WarmUpTests
{
    [Fact]
    public async Task Every_enabled_account_is_brought_up_and_a_disabled_one_is_left_alone()
    {
        await using var launcher = new FixtureLauncher();
        using var factory = new DispatchFactory(launcher);
        Guid on, off;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var a = new ProviderAccount { Label = "on", ProfileDirectory = "on", DailyCap = 25 };
            var b = new ProviderAccount { Label = "off", ProfileDirectory = "off", DailyCap = 25, IsEnabled = false };
            db.Accounts.AddRange(a, b); await db.SaveChangesAsync();
            (on, off) = (a.Id, b.Id);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var brought = await GenerationWorker.ConnectAllAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), factory.Services.GetRequiredService<ProviderRegistry>(), NullLogger.Instance, default);
            Assert.Equal(1, brought);
        }
        var pool = factory.Services.GetRequiredService<BrowserPool>();
        Assert.Equal("Connected", pool.StateOf(on));
        Assert.Equal("Disconnected", pool.StateOf(off));
        Assert.Single(launcher.Contexts);
    }

    [Fact]
    public async Task One_account_that_cannot_come_up_does_not_stop_the_others()
    {
        await using var launcher = new FixtureLauncher();
        using var factory = new DispatchFactory(launcher);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // A profile path that resolves outside the profiles folder is refused before any browser starts: a stand-in for an account that fails.
            db.Accounts.Add(new ProviderAccount { Label = "broken", ProfileDirectory = "../outside", DailyCap = 25, LastUsedAt = DateTime.UtcNow.AddHours(-2) });
            db.Accounts.Add(new ProviderAccount { Label = "fine", ProfileDirectory = "fine", DailyCap = 25 });
            await db.SaveChangesAsync();
        }
        using (var scope = factory.Services.CreateScope())
            await GenerationWorker.ConnectAllAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), factory.Services.GetRequiredService<ProviderRegistry>(), NullLogger.Instance, default);
        using (var scope = factory.Services.CreateScope())
        {
            var fine = scope.ServiceProvider.GetRequiredService<AppDbContext>().Accounts.Single(a => a.Label == "fine").Id;
            Assert.Equal("Connected", factory.Services.GetRequiredService<BrowserPool>().StateOf(fine));
        }
    }
}
