using System.Net;
using System.Text;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// A loopback HTTP stand-in for an API server and the kubeconfig that points a real
/// <see cref="ClusterClient"/> at it — the App-test twin of the one in
/// <c>ApplyPreviewHttpTests</c>, for view models whose behaviour depends on what the server
/// answers each request (a bulk delete that is refused for some objects and not others, a
/// new document's apply path). Every request is recorded; anything without a canned answer
/// gets a 404 Status, which is what a real server says about an object that is not there.
/// </summary>
internal sealed class StubApiServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = new(StringComparer.Ordinal);
    private readonly List<(string Method, string Path, string Query, string Body)> _requests = [];
    private readonly string _directory;
    private readonly Task _pump;

    /// <summary>When set, every request waits for it before being answered — a request held in flight.</summary>
    public TaskCompletionSource<bool>? Gate { get; set; }

    /// <summary>Completed when a request arrives while <see cref="Gate"/> holds it.</summary>
    public TaskCompletionSource<bool> Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StubApiServer()
    {
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _pump = Task.Run(PumpAsync);

        _directory = Directory.CreateTempSubdirectory("kubenimbus-app-stub").FullName;
        File.WriteAllText(Path.Combine(_directory, "kubeconfig.yaml"), $$"""
            apiVersion: v1
            kind: Config
            clusters:
            - name: stub
              cluster:
                server: http://127.0.0.1:{{port}}
            contexts:
            - name: stub
              context:
                cluster: stub
                user: stub
            current-context: stub
            users:
            - name: stub
              user:
                token: not-a-credential
            """);
    }

    public IReadOnlyList<(string Method, string Path, string Query, string Body)> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public void Respond(string method, string path, HttpStatusCode status, string body) =>
        _responses[$"{method} {path}"] = (status, body);

    private readonly List<(string Method, string Path, string QueryPart, HttpStatusCode Status, string Body)> _queryResponses = [];

    /// <summary>
    /// An answer for a request whose query contains <paramref name="queryPart"/> (a forced
    /// dry run, say), checked before the answers <see cref="Respond"/> gives by path alone.
    /// </summary>
    public void RespondWhenQueryContains(string method, string path, string queryPart, HttpStatusCode status, string body) =>
        _queryResponses.Add((method, path, queryPart, status, body));

    public ClusterClient Connect() => ClusterClient.Connect(new ClusterContext(
        "stub", "stub", null, "stub", Path.Combine(_directory, "kubeconfig.yaml")));

    /// <summary>The Status body a real API server sends with a 403, naming the subject, verb and resource.</summary>
    public static string Forbidden(string name) =>
        $$"""{"kind":"Status","apiVersion":"v1","status":"Failure","message":"pods \"{{name}}\" is forbidden: User \"system:serviceaccount:bundle-l:narrow\" cannot delete resource \"pods\" in API group \"\" in the namespace \"payments\"","reason":"Forbidden","code":403}""";

    public const string Deleted = """{"kind":"Status","apiVersion":"v1","status":"Success","code":200}""";

    private async Task PumpAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => AnswerAsync(context));
        }
    }

    private async Task AnswerAsync(HttpListenerContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var path = context.Request.Url!.AbsolutePath;
            lock (_requests)
            {
                _requests.Add((context.Request.HttpMethod, path, context.Request.Url.Query, body));
            }

            if (Gate is { } gate)
            {
                Held.TrySetResult(true);
                await gate.Task;
            }

            var query = context.Request.Url.Query;
            var byQuery = _queryResponses.FirstOrDefault(r => r.Method == context.Request.HttpMethod && r.Path == path
                && query.Contains(r.QueryPart, StringComparison.Ordinal));
            var (status, responseBody) = byQuery.Method is not null
                ? (byQuery.Status, byQuery.Body)
                : _responses.TryGetValue($"{context.Request.HttpMethod} {path}", out var canned)
                ? canned
                : (HttpStatusCode.NotFound, """{"kind":"Status","apiVersion":"v1","status":"Failure","reason":"NotFound","code":404}""");
            var bytes = Encoding.UTF8.GetBytes(responseBody);
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
        catch (Exception)
        {
            // The client went away (a cancelled request); nothing is waiting for this answer.
        }
    }

    public void Dispose()
    {
        Gate?.TrySetResult(true);
        _listener.Close();
        try
        {
            _pump.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // The pump ends by the listener being closed under it.
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
