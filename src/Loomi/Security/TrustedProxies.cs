using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;
namespace Loomi.Security;
/// <summary>Who may say where a request really came from. A proxy whose address is fixed goes in Security:KnownProxies; one that is reattached
/// to its network with a new address after every restart, like Harbora's Traefik, can only be named by the range it lives in, Security:KnownNetworks.
/// Without either, every person behind the proxy shares one address, and so one login rate limit.</summary>
public static class TrustedProxies
{
    public sealed record Trust(IPAddress[] Proxies, IPNetwork[] Networks);
    /// <summary>Parsed at startup, so a typo stops the app with its reason rather than failing each request once options are first read.</summary>
    public static Trust Read(IConfiguration config)
    {
        var proxies = (config.GetSection("Security:KnownProxies").Get<string[]>() ?? []).Select(v => IPAddress.TryParse(v, out var ip) ? ip
            : throw new InvalidOperationException($"Security:KnownProxies holds \"{v}\", which is not an IP address.")).ToArray();
        var networks = (config.GetSection("Security:KnownNetworks").Get<string[]>() ?? []).Select(v => IPNetwork.TryParse(v, out var net) ? net
            : throw new InvalidOperationException($"Security:KnownNetworks holds \"{v}\", which is not a network in CIDR form such as 172.16.0.0/12.")).ToArray();
        return new(proxies, networks);
    }
    /// <summary>Traefik reports a WebSocket over TLS as X-Forwarded-Proto: wss. The page that opened it was https, and the WebSocket origin check
    /// compares against the page's origin, so taken as it came it refused every socket: the remote desktop, which is nothing but one, and SignalR's.</summary>
    public static string PageScheme(string scheme) => scheme switch { "wss" => "https", "ws" => "http", _ => scheme };
    public static void Apply(ForwardedHeadersOptions options, Trust trust)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        foreach (var proxy in trust.Proxies) options.KnownProxies.Add(proxy);
        foreach (var network in trust.Networks) options.KnownIPNetworks.Add(network);
    }
}
