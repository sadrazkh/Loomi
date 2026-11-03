namespace Loomi.Services;
/// <summary>Moves the owner's sample run along whenever one is on. Everything it knows is in the database, so a restart simply carries on; it waits for the
/// instance lease so that the Loomi being replaced and its successor never both start a sample.</summary>
public class StarterSampleWorker(IServiceScopeFactory scopes, InstanceLease lease, ILogger<StarterSampleWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(10);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await lease.WaitAsync(stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await StepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Sample run step failed ({Type})", ex.GetType().Name); }
            try { await Task.Delay(Every, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
    /// <summary>One look at the run. Public so a test can move it along without waiting.</summary>
    public async Task StepAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<StarterService>().StepAsync(ct);
    }
}
