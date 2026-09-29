using Loomi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Loomi.Tests;
/// <summary>Harbora starts the new container beside the old one on the same volume and kills the old one only once the new one answers. Until then
/// the old instance's runs are live and its browsers hold the profiles, so the newcomer must not sweep or launch anything.</summary>
public sealed class InstanceLeaseTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "loomi-lease-" + Guid.NewGuid());
    private string File => Path.Combine(root, "instance.lease");
    private static readonly TimeSpan Renew = TimeSpan.FromMilliseconds(50), Stale = TimeSpan.FromMilliseconds(1500);
    private InstanceLease Lease(string holder) => new(File, holder, Renew, Stale, NullLogger<InstanceLease>.Instance);
    /// <summary>Written the way the app writes it, aside and renamed, so the reader never sees a torn file this test itself produced.</summary>
    private void LeftBy(string holder, DateTime renewed)
    {
        Directory.CreateDirectory(root);
        System.IO.File.WriteAllLines(File + ".fake", [holder, renewed.ToString("O")]);
        // Windows refuses the replace with either exception while the reader has the file open. Uncaught on this bare thread, it took the whole test host down.
        try { System.IO.File.Move(File + ".fake", File, overwrite: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the next renewal lands */ }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public async Task A_first_start_takes_the_lease_at_once()
    {
        await using var lease = Lease("new");
        await lease.WaitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("new", System.IO.File.ReadAllLines(File)[0]);
    }

    [Fact]
    public async Task A_newcomer_waits_while_the_old_instance_is_still_renewing_and_takes_over_once_it_stops()
    {
        // Its own thread, not the pool: the whole suite runs in parallel, and a starved pool stalling the renewals would look like a kill.
        var killed = false;
        var old = new Thread(() => { while (!Volatile.Read(ref killed)) { LeftBy("old", DateTime.UtcNow); Thread.Sleep(Renew); } }) { IsBackground = true };
        old.Start();
        await Task.Delay(Renew * 2);
        await using var newcomer = Lease("new");
        var waiting = newcomer.WaitAsync();
        await Task.Delay(Stale * 2);
        Assert.False(waiting.IsCompleted);
        // The platform kills the old container: no clean release, the renewals simply stop.
        Volatile.Write(ref killed, true); old.Join();
        var killedAt = DateTime.UtcNow;
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(DateTime.UtcNow - killedAt >= Stale - Renew * 3);
        Assert.Equal("new", System.IO.File.ReadAllLines(File)[0]);
    }

    [Fact]
    public async Task A_lease_left_by_a_container_killed_long_ago_is_taken_over_at_once()
    {
        LeftBy("old", DateTime.UtcNow.AddMinutes(-5));
        await using var lease = Lease("new");
        await lease.WaitAsync().WaitAsync(TimeSpan.FromMilliseconds(400));
    }

    [Fact]
    public async Task Its_own_lease_from_before_a_restart_is_taken_back_at_once()
    {
        LeftBy("same", DateTime.UtcNow);
        await using var lease = Lease("same");
        await lease.WaitAsync().WaitAsync(TimeSpan.FromMilliseconds(400));
    }

    [Fact]
    public async Task A_clean_stop_hands_the_lease_back_so_the_next_start_does_not_wait()
    {
        await using (var lease = Lease("old")) await lease.WaitAsync();
        Assert.False(System.IO.File.Exists(File));
        await using var next = Lease("new");
        await next.WaitAsync().WaitAsync(TimeSpan.FromMilliseconds(400));
    }
}
