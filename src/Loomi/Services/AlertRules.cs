using Loomi.Models;
namespace Loomi.Services;
/// <summary>When the owner should hear about the accounts: the moment something needs a person, once, and again when it recovers. Remembers what it last
/// saw so a state that persists is not repeated every check, and holds back a repeat of the same alert for a while so an account flapping between
/// states does not become a stream of messages.</summary>
public class AlertRules(TimeSpan stallAfter, TimeSpan quietFor)
{
    public const int FailureStreak = 3;
    public readonly record struct AccountView(Guid Id, string Label, string State, bool Spent, bool Capped, int Cap);
    public readonly record struct Alert(string Key, object[] Args);
    private static bool NeedsPerson(string state) => state is "LoginRequired" or "VerificationRequired";
    private readonly object gate = new();
    private readonly Dictionary<Guid, AccountView> seen = [];
    private readonly HashSet<Guid> waitingOnPerson = [];
    private readonly Dictionary<string, DateTime> sent = [];
    private DateTime? stalledSince;
    private bool stallTold, streakTold;
    private int failures;
    private readonly List<string> codes = [];

    /// <summary>One look at every enabled account and the queue. An account seen for the first time is compared with a disconnected one, which is how
    /// every account starts after a restart.</summary>
    public List<Alert> Observe(IReadOnlyList<AccountView> accounts, int queued, bool anyUsable, DateTime now)
    {
        lock (gate)
        {
            var alerts = new List<Alert>();
            foreach (var account in accounts)
            {
                var before = seen.TryGetValue(account.Id, out var known) ? known : account with { State = "Disconnected", Spent = false, Capped = false };
                // Told once while it lasts. One held back because the same alert went out moments ago is told when the quiet period ends, if it still
                // lasts then; and "connected again" follows only an alert that was actually sent.
                if (NeedsPerson(account.State))
                {
                    if (!waitingOnPerson.Contains(account.Id) && Due($"{account.State}:{account.Id}", now)) { alerts.Add(new("alert" + account.State, [account.Label])); waitingOnPerson.Add(account.Id); }
                }
                else if (account.State == "Connected" && waitingOnPerson.Remove(account.Id)) alerts.Add(new("alertBack", [account.Label]));
                if (account.Spent && !before.Spent) alerts.Add(new("alertSpent", [account.Label]));
                if (account.Capped && !before.Capped) alerts.Add(new("alertCapped", [account.Label, account.Cap]));
                seen[account.Id] = account;
            }
            if (queued > 0 && !anyUsable)
            {
                stalledSince ??= now;
                if (!stallTold && now - stalledSince >= stallAfter) { alerts.Add(new("alertStalled", [queued])); stallTold = true; }
            }
            else (stalledSince, stallTold) = (null, false);
            return alerts;
        }
    }
    /// <summary>Failures in a row, whatever their cause, told once per streak. A run cut off by a restart says nothing about the accounts, and a
    /// cancellation is somebody's decision, so neither counts; anything that delivered ends the streak.</summary>
    public Alert? Outcome(RunStatus status, string? error)
    {
        lock (gate)
        {
            if (status is RunStatus.Completed or RunStatus.Answered) { (failures, streakTold) = (0, false); codes.Clear(); return null; }
            if (status != RunStatus.Failed || error == "Interrupted") return null;
            failures++;
            if (error is not null && !codes.Contains(error)) codes.Add(error);
            if (failures < FailureStreak || streakTold) return null;
            streakTold = true;
            return new("alertFailures", [failures, string.Join(", ", codes)]);
        }
    }
    private bool Due(string key, DateTime now)
    {
        if (sent.TryGetValue(key, out var last) && now - last < quietFor) return false;
        sent[key] = now;
        return true;
    }
}
