using System.Text.Json;
using KubeNimbus.Core.Applications;

namespace KubeNimbus.Core.Networking;

/// <summary>Where one Ingress path sends traffic: a Service and port, or a resource backend.</summary>
/// <param name="ServiceName">The backend Service, or null for a resource backend.</param>
/// <param name="ServicePort">"80" or "http" — Ingress may name a Service port by either.</param>
/// <param name="ResourceText">"StorageBucket/static-assets" for a resource backend; empty otherwise.</param>
public sealed record IngressBackend(string? ServiceName, string ServicePort, string ResourceText)
{
    public string Display => ServiceName is { } name
        ? ServicePort.Length > 0 ? $"{name}:{ServicePort}" : name
        : ResourceText.Length > 0 ? ResourceText : "(no backend)";
}

/// <summary>One routable path, flattened from a rule: host, path, backend and how it is reached.</summary>
/// <param name="Host">Empty when the rule names no host (it then matches any host).</param>
/// <param name="Path">Empty for a rule with no <c>http</c> section, which falls through to the default backend.</param>
/// <param name="IsTls">The host appears in <c>spec.tls[].hosts</c>, so the URL is https.</param>
/// <param name="TlsSecret">The secret that TLS entry names; empty when it names none (the
/// controller's default certificate).</param>
/// <param name="Url">An openable URL, or null when the host is not a valid DNS name — a
/// wildcard, an empty host, anything the manifest put there that is not a hostname.</param>
public sealed record IngressPath(
    string Host,
    string Path,
    string PathType,
    IngressBackend Backend,
    bool IsTls,
    string TlsSecret,
    Uri? Url);

/// <summary>An Ingress read into its routes.</summary>
/// <param name="ClassName"><c>spec.ingressClassName</c>, or the legacy
/// <c>kubernetes.io/ingress.class</c> annotation when only that is set; empty when neither is.</param>
/// <param name="TlsHostsWithoutRule">Hosts a TLS entry lists that no rule serves — usually a
/// typo in one of the two places the host has to be spelled.</param>
public sealed record IngressView(
    string ClassName,
    bool ClassFromAnnotation,
    IReadOnlyList<IngressPath> Paths,
    IngressBackend? DefaultBackend,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> TlsHostsWithoutRule);

/// <summary>
/// An Ingress as <c>host/path → backend</c> rows with a TLS state and an openable URL per row.
///
/// <para>
/// <b>This is the first place a cluster-controlled string becomes something the operating
/// system opens</b>, so the URL is built, never copied. The host must be a valid DNS-1123
/// name (lower-case labels of letters, digits and hyphens — what the API server itself
/// validates an Ingress host against), the path goes in only when it is a plain path, and
/// the result is re-parsed and checked to be http/https on exactly that host. A wildcard
/// host, an empty host or anything else renders as plain text with no link, as Headlamp
/// does. A regex path (ingress-nginx's <c>ImplementationSpecific</c> <c>/api(/|$)(.*)</c>)
/// links to the host's root rather than to a URL that would 404.
/// </para>
///
/// <para>
/// <b>https exactly when the host appears in <c>spec.tls[].hosts</c></b>, compared
/// literally. A wildcard TLS entry is not expanded to cover a concrete rule host: whether
/// the controller serves that host with the wildcard certificate is its business, and
/// guessing https for a host that is served plain would be the one wrong link here.
/// </para>
/// </summary>
public static class IngressRules
{
    public static IngressView Read(DynamicResource ingress)
    {
        ArgumentNullException.ThrowIfNull(ingress);

        var spec = J.Obj(ingress.Raw, "spec");

        var className = J.Str(spec, "ingressClassName");
        var fromAnnotation = false;
        if (className.Length == 0
            && ingress.Annotations.TryGetValue("kubernetes.io/ingress.class", out var annotated)
            && annotated.Length > 0)
        {
            className = annotated;
            fromAnnotation = true;
        }

        var tls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in J.Arr(spec, "tls"))
        {
            var secret = J.Str(entry, "secretName");
            foreach (var host in J.Arr(entry, "hosts"))
            {
                if (host.ValueKind == JsonValueKind.String && host.GetString() is { Length: > 0 } name)
                {
                    tls.TryAdd(name, secret);
                }
            }
        }

        var paths = new List<IngressPath>();
        var ruleHosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in J.Arr(spec, "rules"))
        {
            var host = J.Str(rule, "host");
            ruleHosts.Add(host);
            var isTls = host.Length > 0 && tls.ContainsKey(host);
            var secret = isTls ? tls[host] : "";

            var httpPaths = J.Arr(J.Obj(rule, "http"), "paths");
            if (httpPaths.Count == 0)
            {
                paths.Add(new IngressPath(host, "", "", ReadBackend(J.Obj(spec, "defaultBackend")), isTls, secret,
                    BuildUrl(host, "/", isTls)));
                continue;
            }

            foreach (var path in httpPaths)
            {
                var text = J.Str(path, "path");
                var pathType = J.Str(path, "pathType");
                paths.Add(new IngressPath(
                    host,
                    text.Length > 0 ? text : "/",
                    pathType,
                    ReadBackend(J.Obj(path, "backend")),
                    isTls,
                    secret,
                    BuildUrl(host, IsPlainPath(text) ? text : "/", isTls)));
            }
        }

        var defaultBackend = J.Obj(spec, "defaultBackend").ValueKind == JsonValueKind.Object
            ? ReadBackend(J.Obj(spec, "defaultBackend"))
            : null;

        var addresses = new List<string>();
        foreach (var entry in J.Arr(J.Obj(J.Obj(ingress.Raw, "status"), "loadBalancer"), "ingress"))
        {
            if (J.Str(entry, "ip") is { Length: > 0 } ip)
            {
                addresses.Add(ip);
            }
            else if (J.Str(entry, "hostname") is { Length: > 0 } hostname)
            {
                addresses.Add(hostname);
            }
        }

        return new IngressView(
            className,
            fromAnnotation,
            paths,
            defaultBackend,
            addresses,
            [.. tls.Keys.Where(h => !ruleHosts.Contains(h)).Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// kubectl's Hosts column (<c>formatHosts</c>): up to three hosts, "+ N more...", and
    /// <c>*</c> when no rule names one.
    /// </summary>
    public static string HostsColumn(JsonElement spec)
    {
        const int max = 3;
        var rules = J.Arr(spec, "rules");
        var list = new List<string>();
        var more = false;
        foreach (var rule in rules)
        {
            if (list.Count == max)
            {
                more = true;
            }

            if (!more && J.Str(rule, "host") is { Length: > 0 } host)
            {
                list.Add(host);
            }
        }

        if (list.Count == 0)
        {
            return "*";
        }

        var text = string.Join(",", list);
        return more ? $"{text} + {rules.Count - max} more..." : text;
    }

    private static IngressBackend ReadBackend(JsonElement backend)
    {
        var service = J.Obj(backend, "service");
        if (service.ValueKind == JsonValueKind.Object)
        {
            var port = J.Obj(service, "port");
            var portText = J.Int(port, "number") is { } number
                ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : J.Str(port, "name");
            return new IngressBackend(J.Str(service, "name"), portText, "");
        }

        var resource = J.Obj(backend, "resource");
        return resource.ValueKind == JsonValueKind.Object
            ? new IngressBackend(null, "", $"{J.Str(resource, "kind")}/{J.Str(resource, "name")}")
            : new IngressBackend(null, "", "");
    }

    /// <summary>
    /// A DNS-1123 subdomain, which is what the API server requires an Ingress host to be
    /// (a wildcard's <c>*.</c> prefix aside, and a wildcard is not a place a browser can go).
    /// </summary>
    public static bool IsValidHostname(string host)
    {
        if (host.Length is 0 or > 253)
        {
            return false;
        }

        foreach (var label in host.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (var c in label)
            {
                if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>A literal path — letters, digits, <c>-._~/</c> and well-formed percent escapes — that can go in a URL as written.</summary>
    public static bool IsPlainPath(string path)
    {
        if (path.Length == 0 || path[0] != '/')
        {
            return false;
        }

        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '.' or '_' or '~' or '/')
            {
                continue;
            }

            if (c == '%' && i + 2 < path.Length && Uri.IsHexDigit(path[i + 1]) && Uri.IsHexDigit(path[i + 2]))
            {
                i += 2;
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// The URL for one route, or null when it cannot be one. Built from a validated host
    /// and a plain path, then parsed back and checked, so nothing the manifest said can
    /// turn into a different scheme, a different host or a command line.
    /// </summary>
    public static Uri? BuildUrl(string host, string path, bool https)
    {
        if (!IsValidHostname(host) || !IsPlainPath(path))
        {
            return null;
        }

        var scheme = https ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        return Uri.TryCreate($"{scheme}://{host}{path}", UriKind.Absolute, out var uri)
               && uri.Scheme == scheme
               && string.Equals(uri.Host, host, StringComparison.Ordinal)
               && uri.IsDefaultPort
               && uri.UserInfo.Length == 0
            ? uri
            : null;
    }
}
