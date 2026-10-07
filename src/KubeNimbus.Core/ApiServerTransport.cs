using System.Net;
using System.Net.WebSockets;
using System.Text;
using k8s;

namespace KubeNimbus.Core;

/// <summary>
/// A kubeconfig user entry's impersonation — <c>as</c>, <c>as-uid</c>, <c>as-groups</c> and
/// <c>as-user-extra</c> — which kubectl sends as <c>Impersonate-*</c> headers on every request
/// and the client library reads into its model and then never sends.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it matters.</b> Impersonation in a kubeconfig is how a shared admin credential is
/// narrowed: the base identity may impersonate, and the context says who to be. Ignoring it
/// made kubeNimbus act as the base identity, with more rights than kubectl has in the same
/// context — the opposite of what the kubeconfig's author set up.
/// </para>
/// <para>
/// <b>At most one group, and one value per extra key.</b> The API server reads every
/// <c>Impersonate-Group</c> header line as one group and never splits a value on commas.
/// .NET's HTTP client cannot send a header twice: it joins the values into one line
/// (<c>Impersonate-Group: a, b</c>, measured), which the server would read as a single group
/// called "a, b". The WebSocket transport cannot even hold two. Acting as that group would be
/// acting as somebody the kubeconfig did not name, so such an entry is refused with a sentence
/// that says why, rather than half-honoured.
/// </para>
/// </remarks>
/// <param name="User">The user to act as (<c>as</c>).</param>
/// <param name="Uid">The user's UID (<c>as-uid</c>), or null.</param>
/// <param name="Groups">The groups to act in (<c>as-groups</c>).</param>
/// <param name="Extra">Extra user attributes (<c>as-user-extra</c>), a list per key as in client-go.</param>
public sealed record KubeconfigImpersonation(
    string? User,
    string? Uid,
    IReadOnlyList<string> Groups,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Extra)
{
    /// <summary>Whether the entry asks for any impersonation at all.</summary>
    public bool IsEmpty =>
        string.IsNullOrEmpty(User) && string.IsNullOrEmpty(Uid) && Groups.Count == 0 && Extra.Count == 0;

    /// <summary>
    /// Throws <see cref="KubeconfigSetupException"/> for an entry that cannot be honoured
    /// exactly: groups, a UID or extras without a user (client-go refuses that too), or more
    /// values than one header line can carry — see the remarks on the type.
    /// </summary>
    public void Validate(string userEntry)
    {
        if (string.IsNullOrEmpty(User))
        {
            if (!IsEmpty)
            {
                throw new KubeconfigSetupException(
                    $"The kubeconfig user \"{userEntry}\" sets as-uid, as-groups or as-user-extra without as. Impersonation needs a user to act as.");
            }

            return;
        }

        if (Groups.Count > 1)
        {
            throw new KubeconfigSetupException(
                $"The kubeconfig user \"{userEntry}\" impersonates {Groups.Count} groups. kubeNimbus can send only one: .NET joins repeated Impersonate-Group headers into one line, which the API server would read as a single group named \"{string.Join(", ", Groups)}\". Use kubectl for this context, or keep one group in as-groups.");
        }

        foreach (var (key, values) in Extra)
        {
            if (values.Count > 1)
            {
                throw new KubeconfigSetupException(
                    $"The kubeconfig user \"{userEntry}\" gives as-user-extra \"{key}\" {values.Count} values. kubeNimbus can send only one per key, for the same reason it can send only one group.");
            }
        }
    }

    /// <summary>
    /// The headers kubectl sends for this entry, in client-go's spelling: nothing without a
    /// user; otherwise <c>Impersonate-User</c>, then <c>Impersonate-Uid</c>,
    /// <c>Impersonate-Group</c> and one <c>Impersonate-Extra-&lt;key&gt;</c> per value.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers()
    {
        if (string.IsNullOrEmpty(User))
        {
            return [];
        }

        var headers = new List<KeyValuePair<string, string>> { new("Impersonate-User", User) };
        if (!string.IsNullOrEmpty(Uid))
        {
            headers.Add(new("Impersonate-Uid", Uid));
        }

        headers.AddRange(Groups.Select(g => new KeyValuePair<string, string>("Impersonate-Group", g)));
        foreach (var (key, values) in Extra.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            headers.AddRange(values.Select(v => new KeyValuePair<string, string>("Impersonate-Extra-" + EscapeExtraKey(key), v)));
        }

        return headers;
    }

    /// <summary>How the impersonation reads in the connection report: the identity only, never a credential.</summary>
    public string Describe()
    {
        var text = $"\"{User}\"";
        if (!string.IsNullOrEmpty(Uid)) text += $" (uid {Uid})";
        if (Groups.Count > 0) text += $" in {string.Join(", ", Groups.Select(g => $"\"{g}\""))}";
        return text;
    }

    /// <summary>
    /// client-go's <c>headerKeyEscape</c>: every byte of the UTF-8 key outside RFC 3986's
    /// unreserved set is percent-encoded, upper-case. The API server unescapes it with
    /// <c>url.PathUnescape</c>, so <c>acme.com/project</c> arrives as itself.
    /// </summary>
    internal static string EscapeExtraKey(string key)
    {
        var builder = new StringBuilder(key.Length);
        foreach (var b in Encoding.UTF8.GetBytes(key))
        {
            var c = (char)b;
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.' or '~')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// The first handler every HTTP request goes through, in front of the library's
/// <c>SocketsHttpHandler</c> — so it covers the generated calls and
/// <c>ClusterClient.SendRequestAsync</c> alike, since both send through the client's own
/// <c>HttpClient</c>. It sets two things the library leaves undone on our requests:
/// <list type="bullet">
/// <item>the <c>Host</c> header to the cluster's <c>tls-server-name</c>, which is what makes
/// the TLS stack send that name as SNI and check the certificate against it (the library does
/// this only inside its own <c>SendRequestRaw</c>; ours only appeared to work because the
/// library's check ignored a name mismatch);</item>
/// <item>the kubeconfig's impersonation headers.</item>
/// </list>
/// </summary>
internal sealed class ApiServerRequestHandler(string? tlsServerName, KubeconfigImpersonation? impersonation) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Apply(request);
        return base.SendAsync(request, cancellationToken);
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Apply(request);
        return base.Send(request, cancellationToken);
    }

    private void Apply(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(tlsServerName))
        {
            request.Headers.Host = tlsServerName.Trim();
        }

        if (impersonation is null)
        {
            return;
        }

        foreach (var (name, _) in impersonation.Headers().DistinctBy(h => h.Key))
        {
            request.Headers.Remove(name);
        }

        foreach (var (name, value) in impersonation.Headers())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}

/// <summary>
/// The WebSocket transport (exec, port-forward) with the same rules as the HTTP one: the
/// certificate check, the proxy, <c>tls-server-name</c> and impersonation. The library's
/// <c>StreamConnectAsync</c> calls the non-virtual <c>ExpectServerCertificate</c> (the flawed
/// check) and then the virtual <see cref="BuildAndConnectAsync"/>, which is why the validator
/// is installed here, last, just before the socket connects.
/// </summary>
internal sealed class ApiServerWebSocketBuilder : WebSocketBuilder
{
    private readonly ApiServerCertificateValidator? _validator;

    public ApiServerWebSocketBuilder(
        IWebProxy? proxy,
        ApiServerCertificateValidator? validator,
        string? tlsServerName,
        KubeconfigImpersonation? impersonation)
    {
        _validator = validator;
        if (proxy is not null)
        {
            Options.Proxy = proxy;
        }

        if (!string.IsNullOrWhiteSpace(tlsServerName))
        {
            Options.SetRequestHeader("Host", tlsServerName.Trim());
        }

        foreach (var (name, value) in impersonation?.Headers() ?? [])
        {
            Options.SetRequestHeader(name, value);
        }
    }

    public override Task<WebSocket> BuildAndConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        // Null only for insecure-skip-tls-verify (the library's accept-all stays, and the
        // app says so) and for a plain-http server, where there is no certificate.
        if (_validator is not null)
        {
            Options.RemoteCertificateValidationCallback = _validator.Validate;
        }

        return base.BuildAndConnectAsync(uri, cancellationToken);
    }
}
