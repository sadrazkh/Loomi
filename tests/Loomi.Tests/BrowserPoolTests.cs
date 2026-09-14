using Loomi.BrowserAutomation;
using Loomi.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Loomi.Tests;
public class BrowserPoolTests
{
    private static (BrowserPool Pool, string Root) Build(FixtureLauncher launcher, BrowserOptions? options = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "loomi-pool-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Root"] = root }).Build();
        options ??= new BrowserOptions();
        options.Headless = true;
        return (new BrowserPool(Options.Create(options), config, launcher), root);
    }
    private static BrowserAccount Account(string directory) => new() { Label = directory, ProfileDirectory = directory };
    private static string Profiles(string root, string directory) => Path.Combine(root, "profiles", directory);

    [Theory]
    [InlineData("../escape")]
    [InlineData("keep/../../escape")]
    [InlineData("")]
    public void A_crafted_profile_directory_cannot_reach_outside_the_storage_root(string directory)
        => Assert.Equal("InvalidPath", Assert.Throws<InvalidOperationException>(() => BrowserSession.ProfilePath(Path.Combine(Path.GetTempPath(), "loomi-root"), directory)).Message);

    [Fact]
    public void An_absolute_profile_directory_is_rejected_rather_than_followed()
        => Assert.Throws<InvalidOperationException>(() => BrowserSession.ProfilePath(Path.Combine(Path.GetTempPath(), "loomi-root"), Path.Combine(Path.GetTempPath(), "elsewhere")));

    [Fact]
    public async Task Every_account_gets_its_own_browser_under_its_own_profile_folder()
    {
        await using var launcher = new FixtureLauncher();
        var (pool, root) = Build(launcher);
        await using (pool)
        {
            foreach (var account in new[] { Account("one"), Account("two") })
                Assert.Equal("Connected", (await pool.RunAsync(account, s => s.ConnectAsync(default))).State);
            Assert.True(Directory.Exists(Profiles(root, "one")));
            Assert.True(Directory.Exists(Profiles(root, "two")));
            Assert.Equal(2, launcher.Contexts.Count);
        }
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task The_same_account_keeps_one_session_across_calls()
    {
        await using var launcher = new FixtureLauncher();
        var (pool, root) = Build(launcher);
        var account = Account("repeat");
        await using (pool)
        {
            await pool.RunAsync(account, s => s.ConnectAsync(default));
            await pool.RunAsync(account, s => s.ConnectAsync(default));
            Assert.Single(launcher.Contexts);
            Assert.Equal("Connected", pool.StateOf(account.Id));
        }
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task An_account_that_is_working_does_not_hold_up_another_account()
    {
        await using var launcher = new FixtureLauncher();
        var (pool, root) = Build(launcher, new BrowserOptions { GenerationTimeoutSeconds = 120, StableSeconds = 1 });
        var slow = Account("slow");
        var free = Account("free");
        var sending = new TaskCompletionSource();
        using var cancel = new CancellationTokenSource();
        await using (pool)
        {
            var held = pool.RunAsync(slow, s => s.RunAsync(new Generation { Prompt = "timeout" }, null, null,
                status => { if (status == RunStatus.WaitingForResponse) sending.TrySetResult(); return Task.CompletedTask; }, cancel.Token));
            await sending.Task.WaitAsync(TimeSpan.FromSeconds(90));
            Assert.True(pool.IsBusy(slow.Id));
            Assert.False(pool.IsBusy(free.Id));
            var result = await pool.RunAsync(free, s => s.RunAsync(new Generation { Prompt = "quick" }, null, null, _ => Task.CompletedTask, default));
            Assert.True(result.Image.Length > 100);
            Assert.False(pool.IsBusy(free.Id));
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held);
        }
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task Resetting_one_account_clears_only_its_own_profile_and_session()
    {
        await using var launcher = new FixtureLauncher();
        var (pool, root) = Build(launcher);
        var first = Account("first");
        var second = Account("second");
        await using (pool)
        {
            await pool.RunAsync(first, s => s.ConnectAsync(default));
            await pool.RunAsync(second, s => s.ConnectAsync(default));
            await pool.RunAsync(first, s => s.ResetAsync(default));
            Assert.False(Directory.Exists(Profiles(root, "first")));
            Assert.True(Directory.Exists(Profiles(root, "second")));
            Assert.Equal("Disconnected", pool.StateOf(first.Id));
            Assert.Equal("Connected", pool.StateOf(second.Id));
            await pool.DiscardAsync(second.Id);
            Assert.Equal("Disconnected", pool.StateOf(second.Id));
            Assert.False(pool.IsBusy(second.Id));
        }
        Directory.Delete(root, true);
    }
}
