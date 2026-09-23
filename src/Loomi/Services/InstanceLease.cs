namespace Loomi.Services;
/// <summary>Which running Loomi may drive the browsers and repair the queue. A platform that starts the new container beside the old one on the same
/// volume, as Harbora does before it kills the old one without warning, would otherwise open every Chromium profile twice and sweep the old
/// instance's live runs as interrupted. The holder renews a small file under the storage root; a newcomer waits until that stops.</summary>
public sealed class InstanceLease : IAsyncDisposable
{
    private readonly string path;
    private readonly TimeSpan renewEvery, staleAfter;
    private readonly ILogger<InstanceLease> logger;
    private readonly CancellationTokenSource stop = new();
    private readonly Lazy<Task> acquisition;
    private Task? renewing;
    public string Holder { get; }
    public InstanceLease(string path, string holder, TimeSpan renewEvery, TimeSpan staleAfter, ILogger<InstanceLease> logger)
    {
        (this.path, Holder, this.renewEvery, this.staleAfter, this.logger) = (path, holder, renewEvery, staleAfter, logger);
        acquisition = new(() => AcquireAsync(stop.Token));
    }
    /// <summary>Completes once this instance holds the lease. The first caller starts the wait; a caller giving up does not stop it for the others.</summary>
    public Task WaitAsync(CancellationToken ct = default) => acquisition.Value.WaitAsync(ct);
    private async Task AcquireAsync(CancellationToken ct)
    {
        var told = false;
        while (true)
        {
            (string Holder, DateTime Renewed)? current;
            try { current = Read(); }
            catch (IOException) { await Task.Delay(renewEvery, ct); continue; }   // the other instance is replacing the file this very moment
            if (current is not { } held || held.Holder == Holder || DateTime.UtcNow - held.Renewed >= staleAfter) break;
            if (!told) { logger.LogInformation("Waiting for the previous instance ({Holder}) to let go of the browsers.", held.Holder); told = true; }
            await Task.Delay(renewEvery, ct);
        }
        Write();
        renewing = RenewAsync(ct);
    }
    private async Task RenewAsync(CancellationToken ct)
    {
        try { while (true) { await Task.Delay(renewEvery, ct); try { Write(); } catch (IOException ex) { logger.LogWarning("Could not renew the instance lease ({Type})", ex.GetType().Name); } } }
        catch (OperationCanceledException) { }
    }
    private (string Holder, DateTime Renewed)? Read()
    {
        try
        {
            var lines = File.ReadAllLines(path);
            return lines.Length >= 2 && DateTime.TryParse(lines[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out var renewed) ? (lines[0], renewed.ToUniversalTime()) : null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    /// <summary>Written aside and renamed into place, so a reader never catches it half-written and mistakes a live holder for none.</summary>
    private void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var aside = path + "." + Holder + ".tmp";
        File.WriteAllLines(aside, [Holder, DateTime.UtcNow.ToString("O")]);
        File.Move(aside, path, overwrite: true);
    }
    /// <summary>A clean stop hands the lease straight back, so the next start does not wait out a lease nobody is renewing.</summary>
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        if (renewing is not null) await renewing;
        try { if (Read()?.Holder == Holder) File.Delete(path); } catch (IOException) { /* the next holder waits it out instead */ }
        stop.Dispose();
    }
}
