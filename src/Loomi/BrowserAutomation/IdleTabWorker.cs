namespace Loomi.BrowserAutomation;

/// <summary>Looks over the accounts' tabs once a minute; each decides for itself whether it has sat long enough to clean up.</summary>
public sealed class IdleTabWorker(BrowserPool pool, ILogger<IdleTabWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); await pool.TrimIdleAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { logger.LogWarning("Looking over the idle tabs failed ({Type})", e.GetType().Name); }
        }
    }
}
