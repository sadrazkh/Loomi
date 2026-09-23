using System.Collections.Concurrent;
using Loomi.Models;
namespace Loomi.Services;
/// <summary>Where each waiting request stands: how many are ahead of it and roughly when its turn comes. Only those two numbers ever leave here, never
/// whose requests are ahead. Timings are the last few real runs of each provider, kept in memory; after a restart a fixed guess stands in until work finishes.</summary>
public class QueueEstimates
{
    public readonly record struct Waiting(Guid Id, Guid User, Provider Provider);
    /// <summary>EtaSeconds is the wait until the request's turn starts, or null while no account can take it at all.</summary>
    public readonly record struct Position(Guid Id, int Ahead, int? EtaSeconds);
    private static readonly Dictionary<Provider, double> Guess = new() { [Provider.ChatGPT] = 60, [Provider.Gemini] = 20 };
    private const int Remembered = 20;
    /// <summary>Estimates move in steps this size, so a countdown does not become a message every second.</summary>
    private const int Step = 30;
    private readonly ConcurrentDictionary<Provider, ConcurrentQueue<double>> durations = new();
    private readonly ConcurrentDictionary<Guid, (Provider Provider, DateTime Started)> running = new();
    private IReadOnlyDictionary<Guid, Position[]> byUser = new Dictionary<Guid, Position[]>();

    /// <summary>Takes turns between people. A person's n-th waiting request ranks n, counting the ones of theirs already running, so ten requests from one
    /// person cannot hold everyone else back; within a rank, whoever asked first goes first.</summary>
    public static List<T> FairOrder<T>(IEnumerable<T> queued, Func<T, Guid> user, Func<T, DateTime> created, IReadOnlyDictionary<Guid, int> active)
    {
        var taken = new Dictionary<Guid, int>(active);
        var ranked = queued.OrderBy(created).Select(q => { var u = user(q); var rank = taken.GetValueOrDefault(u); taken[u] = rank + 1; return (Item: q, Rank: rank); }).ToList();
        return ranked.OrderBy(x => x.Rank).ThenBy(x => created(x.Item)).Select(x => x.Item).ToList();
    }
    /// <summary>Plays the queue forward on the accounts that can take each provider's work: every request goes to whichever lane frees up first.
    /// A lane is the seconds until that account is free, zero when it is idle.</summary>
    public static Dictionary<Guid, Position> Estimate(IReadOnlyList<Waiting> order, IReadOnlyDictionary<Provider, List<double>> lanes, Func<Provider, double> average)
    {
        var result = new Dictionary<Guid, Position>();
        foreach (var provider in order.Select(w => w.Provider).Distinct())
        {
            var free = lanes.TryGetValue(provider, out var l) ? l.ToList() : [];
            var ahead = 0;
            foreach (var waiting in order.Where(w => w.Provider == provider))
            {
                int? eta = null;
                if (free.Count > 0)
                {
                    var soonest = free.IndexOf(free.Min());
                    eta = (int)Math.Ceiling(free[soonest] / Step) * Step;
                    free[soonest] += average(provider);
                }
                result[waiting.Id] = new(waiting.Id, ahead++, eta);
            }
        }
        return result;
    }
    public void Started(Guid account, Provider provider, DateTime now) => running[account] = (provider, now);
    /// <summary>Only work that came back with an answer is a timing worth learning from; a timeout would teach the estimate that everything takes ten minutes.</summary>
    public void Finished(Guid account, bool answered, DateTime now)
    {
        if (!running.TryRemove(account, out var run) || !answered) return;
        var recent = durations.GetOrAdd(run.Provider, _ => new());
        recent.Enqueue((now - run.Started).TotalSeconds);
        while (recent.Count > Remembered) recent.TryDequeue(out _);
    }
    public double Average(Provider provider) => durations.TryGetValue(provider, out var recent) && !recent.IsEmpty ? recent.Average() : Guess.GetValueOrDefault(provider, 60);
    /// <summary>Seconds until a busy account is free again: the typical run less what this one has already spent, never quite zero while it still runs.</summary>
    public double Remaining(Guid account, DateTime now) =>
        running.TryGetValue(account, out var run) ? Math.Max(Average(run.Provider) - (now - run.Started).TotalSeconds, 5) : 0;
    public bool IsRunning(Guid account) => running.ContainsKey(account);
    /// <summary>Keeps the latest positions and says whose changed since the last time, so only those people are sent anything.</summary>
    public List<(Guid User, Position[] Positions)> Publish(IReadOnlyList<Waiting> order, Dictionary<Guid, Position> positions)
    {
        var next = order.GroupBy(w => w.User).ToDictionary(g => g.Key, g => g.Select(w => positions[w.Id]).ToArray());
        var previous = Interlocked.Exchange(ref byUser, next);
        return previous.Keys.Union(next.Keys)
            .Where(u => !(previous.GetValueOrDefault(u) ?? []).SequenceEqual(next.GetValueOrDefault(u) ?? []))
            .Select(u => (u, next.GetValueOrDefault(u) ?? [])).ToList();
    }
    public Position[] For(Guid user) => byUser.GetValueOrDefault(user) ?? [];
}
