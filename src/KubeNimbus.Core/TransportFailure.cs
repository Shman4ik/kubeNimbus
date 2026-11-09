using System.Net.Http;
using System.Runtime.ExceptionServices;

namespace KubeNimbus.Core;

/// <summary>
/// Requests that go out together to a server that cannot be reached all fail, but under
/// HTTP/2 not at the same moment. <see cref="SocketsHttpHandler"/> makes one connection
/// attempt at a time for an HTTP/2 pool, and an attempt that fails fails only one of the
/// requests waiting on it; the next request waits for the next attempt. On Windows a refused
/// port takes 2 s per attempt, so the third request of a connect heard its failure 6 s in,
/// and an address that does not answer takes about 21 s per attempt. Over HTTP/1.1 each
/// request had a connection of its own and they all failed together.
/// </summary>
/// <remarks>
/// The connect sends <c>/version</c>, the namespace list and the Helm probe together, and
/// <c>/version</c> is the request whose failure is reported. With <see cref="FirstOf"/> the
/// connect reports whichever of them hears the transport failure first, which is what 1.0
/// did over HTTP/1.1: a refused sandbox read "Connection failed" after 2.4 s in 1.0 and after
/// 4.4 s in the first 1.1 build.
/// </remarks>
public static class TransportFailure
{
    /// <summary>
    /// True when <paramref name="error"/> says the server was not reached at all — no
    /// connection, no name, no TLS session, no proxy tunnel — as opposed to a server that
    /// answered with an error.
    /// </summary>
    public static bool Is(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { StatusCode: null } http
                && http.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
                    or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The result of <paramref name="main"/>, unless one of <paramref name="companions"/>, sent
    /// with it, fails first with a transport failure (<see cref="Is"/>): then that failure,
    /// thrown as it was. A companion that succeeds, or fails in any other way (a 403 on the
    /// namespace list), is ignored and left to its own caller.
    /// </summary>
    public static async Task<T> FirstOf<T>(Task<T> main, params Task[] companions)
    {
        var pending = new List<Task>(companions.Length + 1) { main };
        pending.AddRange(companions);
        while (true)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            if (ReferenceEquals(done, main))
            {
                return await main.ConfigureAwait(false);
            }

            if (done.Exception?.InnerException is { } error && Is(error))
            {
                ExceptionDispatchInfo.Throw(error);
            }

            pending.Remove(done);
        }
    }
}
