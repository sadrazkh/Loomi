using System.Collections.Concurrent;
namespace Loomi.Services;
/// <summary>Which ChatGPT accounts have already spent today's image allowance. Held in memory on purpose: the allowance is the site's, not ours, and a restart is a fair moment to try again.</summary>
public class AccountHealth
{
    private readonly ConcurrentDictionary<Guid, DateTime> spent = new();
    public void MarkSpent(Guid account) => spent[account] = DateTime.Today;
    public bool IsSpent(Guid account) => spent.TryGetValue(account, out var day) && day == DateTime.Today;
}
