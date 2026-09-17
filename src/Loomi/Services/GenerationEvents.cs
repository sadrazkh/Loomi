using Loomi.Models;
namespace Loomi.Services;
/// <summary>What moved, flattened off the entity: a subscriber runs after the run's own context is done with the row and must not hold a reference to it.</summary>
public readonly record struct GenerationEvent(Guid Id, Guid ProjectId, Guid UserId, RunStatus Status, string? ImagePath, string? ErrorMessage, string Prompt)
{
    public static GenerationEvent From(Generation g) => new(g.Id, g.ProjectId, g.UserId, g.Status, g.LocalImagePath, g.ErrorMessage, g.Prompt);
}
/// <summary>An announcement inside the process that a generation moved. The SignalR notification is untouched and stays what the browser listens to;
/// this is how anything else in the server — the bot — hears the same thing without a loop that asks the database over and over.</summary>
public class GenerationEvents
{
    private event Func<GenerationEvent, CancellationToken, Task>? Handlers;
    public void Subscribe(Func<GenerationEvent, CancellationToken, Task> handler) => Handlers += handler;
    public void Unsubscribe(Func<GenerationEvent, CancellationToken, Task> handler) => Handlers -= handler;
    /// <summary>A subscriber that throws is a subscriber's problem: the run that published has already recorded its outcome and must not be failed by a listener.</summary>
    public async Task PublishAsync(GenerationEvent moved, CancellationToken ct)
    {
        foreach (var handler in Handlers?.GetInvocationList() ?? [])
            try { await ((Func<GenerationEvent, CancellationToken, Task>)handler)(moved, ct); } catch { /* the outcome is already written; a listener cannot undo it */ }
    }
}
