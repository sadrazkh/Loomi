using Loomi.BrowserAutomation;
using Loomi.Models;
namespace Loomi.Providers;
/// <summary>ChatGPT reached through a driven browser: this is only the thin seam between the provider contract and the existing pool.</summary>
public sealed class ChatGptProvider(BrowserPool pool) : IImageProvider
{
    public Provider Provider => Provider.ChatGPT;
    public bool IsBusy(ProviderAccount account) => pool.IsBusy(account.Id);
    public string StateOf(ProviderAccount account) => pool.StateOf(account.Id);
    public Task<string?> DesktopUrlAsync() => pool.DesktopUrlAsync();
    public Task<ConnectionStatus> StatusAsync(ProviderAccount account) => pool.RunAsync(account, s => s.StatusAsync());
    public Task<ConnectionStatus> ConnectAsync(ProviderAccount account, CancellationToken ct) => pool.RunAsync(account, s => s.ConnectAsync(ct));
    public Task ResetAsync(ProviderAccount account, CancellationToken ct) => pool.RunAsync(account, s => s.ResetAsync(ct));
    public Task<BrowserResult> RunAsync(ProviderAccount account, GenerationRequest request, ReportStatus report, CancellationToken ct)
        => pool.RunAsync(account, s => s.RunAsync(request.Operation, request.Prompt, request.InputPaths, request.ParentConversation, report, ct));
    public Task DiscardAsync(Guid accountId) => pool.DiscardAsync(accountId);
}
