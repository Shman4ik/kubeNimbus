using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #303 after #338: the Argo dashboard reads the shared watch (<see cref="WatchCache"/>), so its
/// Refresh has to relist that entry. Re-subscribing alone is answered from the cache, and a
/// stale or wedged Applications watch would survive the Refresh button. A real
/// <see cref="ClusterClient"/> talks to a fake API server that counts the Application list
/// requests: Refresh with the dashboard open must cause a second one.
/// </summary>
public class ArgoDashboardRefreshTests
{
    private static readonly ResourceDescriptor Applications =
        new(ArgoCd.Group, "v1alpha1", "Application", "applications", "application", true, ["app"], []);

    [Test]
    public async Task Refresh_on_the_dashboard_lists_the_applications_again()
    {
        TestObjects.RedirectStores();
        await using var server = new ArgoListServer();
        var context = new ClusterContext("argo", "argo", null, "tester", server.Kubeconfig());
        var tab = new ClusterTabViewModel(context);
        tab.Client = await ClusterClient.ConnectAsync(context);
        tab.IsConnected = true;

        var section = new SidebarSectionViewModel(SidebarGrouping.ArgoSection);
        section.Kinds.Add(new SidebarKindViewModel(Applications, "argo"));
        SidebarGrouping.AddArgoDashboard([section], [Applications]);
        tab.SidebarSections.Add(section);
        tab.SelectKindCommand.Execute(section.Kinds.First(k => k.IsArgoDashboard));

        // The first list completed and the watch after it started: the shared entry is synced,
        // so a reader that re-subscribes would be replayed what it holds with no new list.
        await WaitAsync(() => server.Lists >= 1 && server.Watches >= 1, "the dashboard's first list and watch");

        tab.RefreshCommand.Execute(null);

        await WaitAsync(() => server.Lists >= 2, "Refresh to list the Applications again");
        await Assert.That(server.Lists).IsEqualTo(2);
    }

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(50);
        }
    }
}

/// <summary>
/// An API server that answers <c>/version</c>, answers a list of Argo CD Applications with an
/// empty list, holds every watch open, and counts the lists and watches it was asked for.
/// </summary>
internal sealed class ArgoListServer : IAsyncDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private int _lists;
    private int _watches;

    public ArgoListServer()
    {
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Lists => Volatile.Read(ref _lists);

    public int Watches => Volatile.Read(ref _watches);

    private int Port => ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;

    public string Kubeconfig()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kubenimbus-app-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "kubeconfig.yaml");
        File.WriteAllText(path, $"""
            apiVersion: v1
            kind: Config
            clusters:
              - name: argo
                cluster:
                  server: http://127.0.0.1:{Port}
            contexts:
              - name: argo
                context:
                  cluster: argo
                  user: tester
            current-context: argo
            users:
              - name: tester
                user:
                  token: not-a-credential
            """);
        return path;
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = ServeAsync(connection);
            }
        }
        catch (Exception)
        {
            // stopped
        }
    }

    private async Task ServeAsync(System.Net.Sockets.TcpClient connection)
    {
        using var _ = connection;
        try
        {
            var stream = connection.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (await reader.ReadLineAsync(_stop.Token) is { } requestLine)
            {
                while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 })
                {
                    // headers; no request this server answers has a body
                }

                var path = requestLine.Split(' ')[1];
                string body;
                if (path.StartsWith("/version", StringComparison.Ordinal))
                {
                    body = """{"major":"1","minor":"31","gitVersion":"v1.31.0","platform":"linux/amd64"}""";
                }
                else if (path.StartsWith("/apis/argoproj.io/v1alpha1/applications", StringComparison.Ordinal)
                         && path.Contains("watch=true", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _watches);
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                    return;
                }
                else if (path.StartsWith("/apis/argoproj.io/v1alpha1/applications?", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _lists);
                    body = """{"apiVersion":"argoproj.io/v1alpha1","kind":"ApplicationList","metadata":{"resourceVersion":"7"},"items":[]}""";
                }
                else
                {
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                    return;
                }

                var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n{body}";
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(response), _stop.Token);
            }
        }
        catch (Exception)
        {
            // stopped, or the client hung up
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
    }
}
