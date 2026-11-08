using System.Net;

namespace KubeNimbus.Core;

/// <summary>
/// The HTTP version kubeNimbus asks an API server for, and the HTTP/2 settings of the
/// handler every request goes through.
/// </summary>
/// <remarks>
/// <para>
/// <b>HTTP/2, the way kubectl and client-go connect.</b> Every request is built by
/// <see cref="ClusterClient.SendRequestAsync"/>, and an <see cref="HttpRequestMessage"/> built
/// by hand is HTTP/1.1 unless it says otherwise. <c>HttpClient.DefaultRequestVersion</c> does
/// not apply to it, and the client library sets 2.0 only on its own typed calls, which
/// kubeNimbus does not use. Under HTTP/1.1 every request in flight needs a connection of
/// its own, so a connect opened about a dozen: <c>/version</c>, discovery's two, the
/// namespace list, the Helm probe and the Applications mode's seven watches, each with its
/// own TCP and TLS handshake (two round trips before the request is even sent). Each open
/// watch then held its connection for as long as it ran. Under HTTP/2 they all share the
/// connection <c>/version</c> opened. The measurements are on
/// <c>docs/engineering/startup-performance.md</c>.
/// </para>
/// <para>
/// <b>Or lower.</b> The version is negotiated through TLS ALPN, so a server or a
/// TLS-terminating proxy that offers only HTTP/1.1 gets HTTP/1.1, as does a plain
/// <c>http://</c> server. Setting <c>DISABLE_HTTP2</c> to any value asks for HTTP/1.1
/// everywhere. That is the variable client-go reads for the same purpose, so a user whose
/// proxy mishandles HTTP/2 can turn it off for kubectl and kubeNimbus with one setting.
/// </para>
/// <para>
/// <b>Pings.</b> One shared connection also means a connection that dies silently (a NAT
/// entry expired, the VPN switched networks) would stall every watch at once until TCP
/// gives up, which can take hours. With requests open, the handler sends an HTTP/2 PING
/// after <see cref="PingDelay"/> without a frame and drops the connection when no answer
/// comes within <see cref="PingTimeout"/>. Every watch on it then fails and reconnects
/// through its usual path, which reports the loss in the UI (hard rule 2). These are
/// client-go's own values: <c>ReadIdleTimeout</c> 30 s and <c>PingTimeout</c> 15 s.
/// HTTP/1.1 had no such check at all.
/// </para>
/// <para>
/// <b>More than one connection when needed.</b> A server limits the streams one connection
/// may carry (the Go HTTP/2 server's default is 250). Without
/// <see cref="SocketsHttpHandler.EnableMultipleHttp2Connections"/> a request past that limit
/// would wait for a stream to finish, and a watch never finishes, so a busy window of tabs
/// and panes could hang. With it the handler opens another connection instead.
/// </para>
/// <para>
/// <b>A 4 MiB stream window.</b> HTTP/2 lets a server send only as much of a response as the
/// client's window for that stream allows, then waits for the client to widen it. .NET
/// starts the window at 64 KiB and grows it with the measured round trip, so a list of a few
/// hundred kilobytes (the sandbox's 47 pods are 290 KB of JSON) waited for several window
/// updates, a round trip each. At 150 ms that left the first pod list slower than over
/// HTTP/1.1. <see cref="StreamWindow"/> is the window client-go's transport opens with
/// (<c>transportDefaultStreamFlow</c>, 4 MiB), which a list of that size never exhausts.
/// It is a ceiling on unread data per stream, not memory taken up front, and every stream
/// kubeNimbus opens is read as fast as it arrives.
/// </para>
/// </remarks>
internal static class ApiServerHttpVersion
{
    /// <summary>The variable that turns HTTP/2 off. client-go reads the same one.</summary>
    internal const string DisableVariable = "DISABLE_HTTP2";

    internal static readonly TimeSpan PingDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Each stream's initial receive window; see the class remarks.</summary>
    internal const int StreamWindow = 4 << 20;

    /// <summary>The version to ask for, given <c>DISABLE_HTTP2</c>'s value: any non-empty value disables HTTP/2, as in client-go.</summary>
    internal static Version For(string? disableHttp2) =>
        string.IsNullOrEmpty(disableHttp2) ? HttpVersion.Version20 : HttpVersion.Version11;

    /// <summary>The version this process asks for.</summary>
    internal static Version Current => For(Environment.GetEnvironmentVariable(DisableVariable));

    /// <summary>The handler settings HTTP/2 needs; see the class remarks.</summary>
    internal static void Configure(SocketsHttpHandler handler)
    {
        handler.EnableMultipleHttp2Connections = true;
        handler.InitialHttp2StreamWindowSize = StreamWindow;
        handler.KeepAlivePingDelay = PingDelay;
        handler.KeepAlivePingTimeout = PingTimeout;
        handler.KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests;
    }

    /// <summary>A request asking for <paramref name="version"/>, or lower if the server does not offer it.</summary>
    internal static HttpRequestMessage NewRequest(HttpMethod method, Uri uri, HttpContent? content, Version version) =>
        new(method, uri)
        {
            Content = content,
            Version = version,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
}
