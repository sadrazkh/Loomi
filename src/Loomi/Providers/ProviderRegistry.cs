using Loomi.Models;
namespace Loomi.Providers;
/// <summary>Finds the provider for an account. Everything that runs work goes through here rather than naming a provider directly.</summary>
public class ProviderRegistry(IEnumerable<IImageProvider> providers)
{
    private readonly Dictionary<Provider, IImageProvider> map = providers.ToDictionary(p => p.Provider);
    public IImageProvider For(Provider provider) => map.TryGetValue(provider, out var p) ? p : throw new InvalidOperationException("NoProvider");
    public bool Has(Provider provider) => map.ContainsKey(provider);
    public IReadOnlyCollection<IImageProvider> All => map.Values;
}
