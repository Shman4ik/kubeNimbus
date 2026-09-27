using System.Net;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace KubeNimbus.Core;

/// <summary>
/// The kubeconfig cluster field <c>proxy-url</c>, which the client library does not read.
/// </summary>
/// <remarks>
/// <para>
/// Confirmed absent from the dependency, not merely unwired: <c>KubernetesClient.Aot</c>'s
/// <c>ClusterEndpoint</c> model has no such property, so its YAML reader drops the field
/// and every request went direct. That is the whole of a private EKS cluster behind an SSH
/// bastion (<c>proxy-url: socks5://localhost:1080</c>) and of a corporate proxy that a GUI
/// app never inherits through <c>HTTPS_PROXY</c>. So the field is read here, from the same
/// file, through YamlDotNet's structural model (the AOT-safe half, as in
/// <see cref="YamlJson"/>), and applied to both transports the client has: the
/// <c>SocketsHttpHandler</c> every request goes through, and the <c>ClientWebSocket</c> the
/// exec pane and port-forward open.
/// </para>
/// <para>
/// Schemes are the ones kubectl accepts plus SOCKS4, which .NET also speaks: http, https,
/// socks4, socks4a, socks5. SOCKS5 sends the host name to the proxy rather than resolving
/// it locally, which is the point of a bastion whose cluster endpoint only resolves on the
/// far side. A <c>user:password@</c> in the URL becomes the proxy's credentials for this
/// connection only — read from the kubeconfig at connect time like every other credential,
/// never kept, and never shown (<see cref="Redact"/>).
/// </para>
/// </remarks>
internal static class KubeconfigProxy
{
    private static readonly string[] SupportedSchemes = ["http", "https", "socks4", "socks4a", "socks5"];

    /// <summary>The <c>proxy-url</c> of cluster <paramref name="clusterName"/> in <paramref name="kubeconfigText"/>, or null.</summary>
    internal static string? Read(string kubeconfigText, string clusterName)
    {
        YamlStream stream;
        try
        {
            stream = new YamlStream();
            stream.Load(new StringReader(kubeconfigText));
        }
        catch (YamlException)
        {
            // The library parsed this file successfully a moment ago; a second parser
            // disagreeing about it is not a reason to fail a connect over a field it may
            // not even carry.
            return null;
        }

        if (stream.Documents.Count == 0
            || stream.Documents[0].RootNode is not YamlMappingNode root
            || Child(root, "clusters") is not YamlSequenceNode clusters)
        {
            return null;
        }

        foreach (var item in clusters)
        {
            if (item is YamlMappingNode entry
                && Child(entry, "name") is YamlScalarNode { Value: var name }
                && string.Equals(name, clusterName, StringComparison.Ordinal)
                && Child(entry, "cluster") is YamlMappingNode cluster
                && Child(cluster, "proxy-url") is YamlScalarNode { Value: { Length: > 0 } url })
            {
                return url.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// A proxy for <paramref name="proxyUrl"/>, or null when there is none. Throws
    /// <see cref="KubeconfigSetupException"/> for a value that cannot be used — silently
    /// connecting direct instead would send traffic somewhere the kubeconfig said not to.
    /// </summary>
    internal static WebProxy? Create(string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            throw new KubeconfigSetupException(
                $"The cluster's proxy-url \"{Redact(proxyUrl)}\" is not a URL kubeNimbus can use (expected something like http://proxy:3128 or socks5://localhost:1080).");
        }

        if (!SupportedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            throw new KubeconfigSetupException(
                $"The cluster's proxy-url uses \"{uri.Scheme}\", which kubeNimbus cannot proxy through. Supported: {string.Join(", ", SupportedSchemes)}.");
        }

        // Scheme, host and port only: GetLeftPart(Authority) keeps a user:password@, and the
        // address is what gets printed wherever a proxy is described.
        var proxy = new WebProxy(new UriBuilder(uri.Scheme, uri.Host, uri.Port).Uri)
        {
            BypassProxyOnLocal = false,
            UseDefaultCredentials = false,
        };

        if (uri.UserInfo is { Length: > 0 } userInfo)
        {
            var colon = userInfo.IndexOf(':', StringComparison.Ordinal);
            var user = Uri.UnescapeDataString(colon < 0 ? userInfo : userInfo[..colon]);
            var password = colon < 0 ? "" : Uri.UnescapeDataString(userInfo[(colon + 1)..]);
            proxy.Credentials = new NetworkCredential(user, password);
        }

        return proxy;
    }

    /// <summary>The proxy URL with any <c>user:password@</c> removed — the only form that is ever displayed.</summary>
    internal static string Redact(string proxyUrl)
    {
        if (Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0)
        {
            return uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
        }

        var at = proxyUrl.LastIndexOf('@');
        var scheme = proxyUrl.IndexOf("://", StringComparison.Ordinal);
        return at > 0 && scheme >= 0 && at > scheme ? proxyUrl[..(scheme + 3)] + proxyUrl[(at + 1)..] : proxyUrl;
    }

    private static YamlNode? Child(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
}

/// <summary>
/// A kubeconfig entry that parses but that kubeNimbus cannot turn into a connection — an
/// unusable <c>proxy-url</c>, for one. The message names the field and says what would work.
/// </summary>
public sealed class KubeconfigSetupException(string message) : Exception(message);
