using System.Collections.Concurrent;
using Loomi.Models;
using Microsoft.Extensions.Options;
namespace Loomi.BrowserAutomation;

/// <summary>Every connected ChatGPT account's browser, one session each, started on first use and shared by all callers.</summary>
public sealed class BrowserPool(IOptions<BrowserOptions> options, IConfiguration config, IChromiumLauncher launcher) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, BrowserSession> sessions = new();
    // A race can build two sessions, but only the winner is ever handed out and a session launches nothing until it is asked to work.
    private BrowserSession Session(ProviderAccount account) => sessions.GetOrAdd(account.Id, _ => new BrowserSession(account.ProfileDirectory ?? throw new InvalidOperationException("NoProfile"), options, config, launcher));
    public Task<T> RunAsync<T>(ProviderAccount account, Func<BrowserSession, Task<T>> work) => work(Session(account));
    public Task RunAsync(ProviderAccount account, Func<BrowserSession, Task> work) => work(Session(account));
    public bool IsBusy(Guid account) => sessions.TryGetValue(account, out var session) && session.Busy;
    public string StateOf(Guid account) => sessions.TryGetValue(account, out var session) ? session.State : "Disconnected";
    public Task<string?> DesktopUrlAsync() => BrowserSession.DesktopUrlAsync(options.Value);
    public async Task DiscardAsync(Guid account) { if (sessions.TryRemove(account, out var session)) await session.DisposeAsync(); }
    public async ValueTask DisposeAsync() { foreach (var account in sessions.Keys) await DiscardAsync(account); }
}
