using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// A plain-HTTP stand-in for an API server (or a proxy) on a loopback port: every request
/// is recorded, and a handler decides the answer. A raw <see cref="TcpListener"/> rather
/// than <c>HttpListener</c>, which on Windows needs a URL reservation (an elevated
/// <c>netsh</c>) to listen on 127.0.0.1 — a test that only passes as administrator is a
/// test that does not run.
/// </summary>
internal sealed class ScriptedApiServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<ScriptedRequest, ScriptedResponse> _handler;
    private readonly X509Certificate2? _certificate;
    private int _handshakeFailures;

    /// <param name="handler">Decides each answer.</param>
    /// <param name="certificate">
    /// When set, the server speaks TLS with this certificate (which must carry its private
    /// key) and <see cref="Url"/> is https — the stand-in for an API server whose certificate
    /// the client has to check.
    /// </param>
    /// <param name="host">The host <see cref="Url"/> names; it always listens on 127.0.0.1.</param>
    public ScriptedApiServer(
        Func<ScriptedRequest, ScriptedResponse> handler, X509Certificate2? certificate = null, string host = "127.0.0.1")
    {
        _handler = handler;
        _certificate = certificate;
        _listener.Start();
        Url = $"{(certificate is null ? "http" : "https")}://{host}:{Port}";
        _ = Task.Run(AcceptLoopAsync);
    }

    public string Url { get; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>TLS handshakes the client abandoned or refused — a refused certificate shows here, never as a request.</summary>
    public int HandshakeFailures => Volatile.Read(ref _handshakeFailures);

    public ConcurrentQueue<ScriptedRequest> Requests { get; } = new();

    /// <summary>A Kubernetes <c>/version</c> body.</summary>
    public const string VersionBody = """{"major":"1","minor":"31","gitVersion":"v1.31.0","platform":"linux/amd64"}""";

    public static ScriptedResponse Unauthorized() => new(401,
        """{"kind":"Status","apiVersion":"v1","status":"Failure","message":"Unauthorized","reason":"Unauthorized","code":401}""");

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient connection;
            try
            {
                connection = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(connection));
        }
    }

    private async Task ServeAsync(TcpClient connection)
    {
        using (connection)
        {
            try
            {
                Stream stream = connection.GetStream();
                if (_certificate is not null)
                {
                    var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                    try
                    {
                        await tls.AuthenticateAsServerAsync(_certificate);
                    }
                    catch (Exception e) when (e is AuthenticationException or IOException)
                    {
                        Interlocked.Increment(ref _handshakeFailures);
                        await tls.DisposeAsync();
                        return;
                    }

                    stream = tls;
                }

                await using var _ = stream;
                var request = await ReadRequestAsync(stream);
                if (request is null)
                {
                    return;
                }

                Requests.Enqueue(request);
                var response = _handler(request);
                var body = Encoding.UTF8.GetBytes(response.Body);
                var head = response.Hold
                    ? $"HTTP/1.1 {response.Status} X\r\nContent-Type: {response.ContentType}\r\nConnection: close\r\n\r\n"
                    : $"HTTP/1.1 {response.Status} X\r\nContent-Type: {response.ContentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token);
                await stream.WriteAsync(body, _stop.Token);
                await stream.FlushAsync(_stop.Token);

                if (response.Hold)
                {
                    // An open watch: the stream stays up, sending nothing, until the test ends.
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<ScriptedRequest?> ReadRequestAsync(Stream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (buffer.Count < 64 * 1024)
        {
            if (await stream.ReadAsync(one) == 0)
            {
                return null;
            }

            buffer.Add(one[0]);
            if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n')
            {
                break;
            }
        }

        var lines = Encoding.ASCII.GetString([.. buffer]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var requestLine = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var raw = new List<KeyValuePair<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                raw.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
            }
        }

        // Drain a body so the client is not left writing into a closed socket.
        if (headers.TryGetValue("Content-Length", out var length) && int.TryParse(length, out var count) && count > 0)
        {
            var body = new byte[count];
            var read = 0;
            while (read < count)
            {
                var n = await stream.ReadAsync(body.AsMemory(read));
                if (n == 0)
                {
                    break;
                }

                read += n;
            }
        }

        return new ScriptedRequest(requestLine[0], requestLine.Length > 1 ? requestLine[1] : "", headers) { HeaderLines = raw };
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}

internal sealed record ScriptedRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers)
{
    public string? Authorization => Headers.TryGetValue("Authorization", out var value) ? value : null;

    /// <summary>Every header line as sent, in order — a header sent twice is two entries here.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> HeaderLines { get; init; } = [];

    /// <summary>The values of every line named <paramref name="name"/>, case-insensitively.</summary>
    public IReadOnlyList<string> Values(string name) =>
        [.. HeaderLines.Where(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value)];
}

internal sealed record ScriptedResponse(int Status, string Body, string ContentType = "application/json", bool Hold = false);
