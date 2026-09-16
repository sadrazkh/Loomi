using Loomi.BrowserAutomation;
using Loomi.Models;
namespace Loomi.Providers;
/// <summary>What the dispatcher hands a provider: everything about the work, none of the database row.</summary>
public record GenerationRequest(Provider Provider, Operation Operation, string Prompt, IReadOnlyList<string> InputPaths, string? ParentConversation);
/// <summary>One way of turning a prompt into an image. A browser drives a site; an API key calls one. The dispatcher only knows this.</summary>
public interface IImageProvider
{
    Provider Provider { get; }
    /// <summary>Cheap, from state already held, so listing accounts and polling status drive no traffic.</summary>
    bool IsBusy(ProviderAccount account);
    string StateOf(ProviderAccount account);
    Task<string?> DesktopUrlAsync();
    Task<ConnectionStatus> StatusAsync(ProviderAccount account);
    Task<ConnectionStatus> ConnectAsync(ProviderAccount account, CancellationToken ct);
    Task ResetAsync(ProviderAccount account, CancellationToken ct);
    Task<BrowserResult> RunAsync(ProviderAccount account, GenerationRequest request, ReportStatus report, CancellationToken ct);
    Task DiscardAsync(Guid accountId);
}
