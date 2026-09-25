using Loomi.Services;
namespace Loomi.Controllers.V1;
/// <summary>Hears that one message moved, for a caller waiting on it. It only ever says "look again": the row is read afresh each time, so an
/// announcement that slips between two reads costs one re-check at most, never a wrong answer. Subscribed before the first read for the same reason.</summary>
internal sealed class Movement : IDisposable
{
    private readonly GenerationEvents events;
    private readonly Guid id;
    // Never disposed: its wait handle is never asked for, and a publisher that already copied the handler list may still release it after Dispose.
    private readonly SemaphoreSlim moved = new(0);
    public Movement(GenerationEvents events, Guid id) { this.events = events; this.id = id; events.Subscribe(OnMoved); }
    private Task OnMoved(GenerationEvent e, CancellationToken _) { if (e.Id == id) moved.Release(); return Task.CompletedTask; }
    /// <summary>True when the message moved within the time given; false when the time ran out first.</summary>
    public Task<bool> WaitAsync(TimeSpan within, CancellationToken ct) => moved.WaitAsync(within > TimeSpan.Zero ? within : TimeSpan.Zero, ct);
    public void Dispose() => events.Unsubscribe(OnMoved);
}
