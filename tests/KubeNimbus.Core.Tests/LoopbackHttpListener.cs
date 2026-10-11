using System.Net;
using System.Net.Sockets;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Starts an <see cref="HttpListener"/> on a loopback port nobody else holds, for the stub API
/// servers (#279).
/// </summary>
/// <remarks>
/// An <see cref="HttpListener"/> cannot be asked for port 0, so the stubs used to probe for a
/// free port with a <see cref="TcpListener"/>, close the probe, and register that port. Between
/// the close and the registration the port was anyone's, and with several test processes on one
/// machine — the backlog sweep ran a worktree's suite per bundle — two stubs could choose the
/// same one: the second registration failed, and the test failed with it once in a few dozen
/// runs. Choosing then binding cannot be made atomic here, so a registration that fails is
/// retried on another port, with a new listener (one whose <c>Start</c> failed is not reused).
/// </remarks>
internal static class LoopbackHttpListener
{
    private const int Attempts = 20;

    public static HttpListener Start(out int port)
    {
        HttpListenerException? last = null;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return listener;
            }
            catch (HttpListenerException ex)
            {
                // Taken between the probe and the registration (Windows: 32 "in use by another
                // process", 183 "conflicts with an existing registration"). Try another.
                last = ex;
                listener.Close();
            }
        }

        throw new InvalidOperationException($"No loopback port could be registered in {Attempts} attempts.", last);
    }

    /// <summary>A bearer token for one stub's kubeconfig, unique to it.</summary>
    public static string NewToken() => $"stub-{Guid.NewGuid():n}";

    /// <summary>
    /// True when the request carries this stub's token. A port is reused once its stub is gone,
    /// and a client of an earlier stub — a watch retrying, a request still in flight, from this
    /// process or another — can reach the next stub on that port. Such a request was recorded
    /// as this test's own, and a test counting its requests counted it (#279). Anything else is
    /// answered 401 and not seen by the test.
    /// </summary>
    public static bool IsFor(HttpListenerContext context, string token)
    {
        if (context.Request.Headers["Authorization"] == $"Bearer {token}")
        {
            return true;
        }

        try
        {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            context.Response.Close();
        }
        catch (Exception)
        {
            // the stray went away first
        }

        return false;
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
