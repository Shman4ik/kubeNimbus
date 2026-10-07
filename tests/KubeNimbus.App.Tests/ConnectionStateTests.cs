using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// FEAT-51 and FEAT-53 at the view-model: a failed connect is a state of the content area
/// with its own explanation and a Retry, never a blank pane with the reason in the status
/// bar; and the Reconnect affordance appears only where re-resolving credentials can help.
/// The failures here are real ones — a kubeconfig pointed at a loopback port nothing
/// listens on — so the report is what <see cref="ConnectionReport"/> makes of an actual
/// refused connection, not a hand-written stand-in.
/// </summary>
[NotInParallel]
public class ConnectionStateTests
{
    [Test]
    public async Task A_failed_connect_is_stated_in_the_content_area_with_what_was_tried()
    {
        TestObjects.RedirectStores();
        var kubeconfig = RefusedKubeconfig();
        var tab = new ClusterTabViewModel(new ClusterContext("refused", "refused", null, "tester", kubeconfig));

        await tab.ConnectCommand.ExecuteAsync(null);

        await Assert.That(tab.IsConnected).IsFalse();
        await Assert.That(tab.IsConnecting).IsFalse();
        await Assert.That(tab.HasConnectionFailure).IsTrue();

        var failure = tab.ConnectionFailure!;
        await Assert.That(failure.Report.Step).IsEqualTo(ConnectionReport.ReachingServer);
        await Assert.That(failure.Headline).Contains("127.0.0.1:1");
        await Assert.That(failure.Facts.Single(f => f.Label == "Kubeconfig").Value).IsEqualTo(kubeconfig);
        await Assert.That(failure.Facts.Single(f => f.Label == "Signs in with").Value).IsEqualTo("bearer token in the kubeconfig");

        // The status bar keeps a short line of its own; the explanation is on the page.
        await Assert.That(tab.Status).StartsWith("Connection failed");
        await Assert.That(tab.Status).DoesNotContain(failure.Detail);

        // Retry is live, and the Applications page shows the same failure rather than its
        // bare "Not connected".
        await Assert.That(failure.RetryCommand.CanExecute(null)).IsTrue();
        await Assert.That(tab.Applications.ShowsConnectionFailure).IsTrue();
        await Assert.That(tab.Applications.IsDisconnected).IsFalse();
    }

    [Test]
    public async Task Retry_replaces_the_failure_and_the_old_one_stops_following_the_tab()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(new ClusterContext("refused", "refused", null, "tester", RefusedKubeconfig()));
        await tab.ConnectCommand.ExecuteAsync(null);
        var first = tab.ConnectionFailure!;
        var notified = 0;
        first.PropertyChanged += (_, _) => notified++;

        await tab.ReconnectCommand.ExecuteAsync(null);
        tab.TerminalNotice = "something happened";

        await Assert.That(tab.ConnectionFailure).IsNotNull();
        await Assert.That(ReferenceEquals(tab.ConnectionFailure, first)).IsFalse();
        await Assert.That(notified).IsEqualTo(0);
    }

    /// <summary>
    /// The tab is connected once <c>/version</c> answers and still connecting while
    /// discovery runs, and the Applications reads start in that gap. On an EKS cluster signed
    /// in through AWS SSO the gap was seconds, and the page drew three states in one cell:
    /// "Connecting to …" (never told it had ended), "No applications found" (nothing was
    /// pending while the catalog was read, so the list counted as empty) and then
    /// "Reading …". Here a server answers <c>/version</c> and never answers anything else,
    /// so the real connect stops exactly in that gap.
    /// </summary>
    [Test]
    public async Task While_the_catalog_is_read_the_page_says_so_and_nothing_else()
    {
        TestObjects.RedirectStores();
        await using var server = new VersionOnlyServer();
        var tab = new ClusterTabViewModel(new ClusterContext("slow", "slow", null, "tester", server.Kubeconfig()));
        var applications = tab.Applications;
        var notified = new List<string?>();
        applications.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        applications.Activate();

        _ = tab.ConnectCommand.ExecuteAsync(null);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!applications.HasStarted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        // HasStarted is set on the connect's thread; let its notifications finish.
        await Task.Delay(100);
        try
        {
            await Assert.That(applications.HasStarted).IsTrue();
            await Assert.That(tab.IsConnecting).IsTrue();

            await Assert.That(applications.IsConnecting).IsFalse();
            await Assert.That(notified).Contains(nameof(ApplicationsViewModel.IsConnecting));
            await Assert.That(applications.IsEmpty).IsFalse();
            await Assert.That(applications.IsLoading).IsTrue();
            await Assert.That(applications.LoadingText).IsEqualTo("Reading the cluster's resource types…");
        }
        finally
        {
            await applications.DisposeAsync();
        }
    }

    /// <summary>
    /// S1-2: a cluster entry with <c>insecure-skip-tls-verify</c> keeps the status bar on
    /// screen, saying so, for as long as the tab is connected — the routine "Connected" line
    /// that hides the bar on a verified tab must not hide this.
    /// </summary>
    [Test]
    public async Task A_tab_connected_without_tls_verification_keeps_saying_so()
    {
        TestObjects.RedirectStores();
        await using var server = new VersionOnlyServer();
        var unverified = new ClusterTabViewModel(new ClusterContext("slow", "slow", null, "tester", server.Kubeconfig(insecureSkipTlsVerify: true)));
        var verified = new ClusterTabViewModel(new ClusterContext("slow", "slow", null, "tester", server.Kubeconfig()));

        _ = unverified.ConnectCommand.ExecuteAsync(null);
        _ = verified.ConnectCommand.ExecuteAsync(null);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(unverified.IsConnected && verified.IsConnected) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await Assert.That(unverified.IsConnected).IsTrue();
        await Assert.That(unverified.IsTlsUnverified).IsTrue();
        await Assert.That(unverified.IsStatusWorthShowing).IsTrue();

        await Assert.That(verified.IsConnected).IsTrue();
        await Assert.That(verified.IsTlsUnverified).IsFalse();
        await Assert.That(verified.IsStatusWorthShowing).IsFalse();
    }

    [Test]
    public async Task Only_a_lost_watch_offers_reconnect_and_a_later_warning_does_not_inherit_it()
    {
        var tab = TestObjects.Tab();

        tab.ConnectionWarning = "Watch connection lost (SocketException); retrying in 2s.";
        tab.ConnectionWarningOffersReconnect = true;
        tab.ConnectionWarning = "Could not list namespaces: forbidden";

        await Assert.That(tab.ConnectionWarningOffersReconnect).IsFalse();
    }

    [Test]
    public async Task The_demo_cluster_has_nothing_to_reconnect()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);

        await Assert.That(tab.ReconnectCommand.CanExecute(null)).IsFalse();
        await Assert.That(tab.HasConnectionFailure).IsFalse();
    }

    private static string RefusedKubeconfig()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kubenimbus-app-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "kubeconfig.yaml");
        File.WriteAllText(path, """
            apiVersion: v1
            kind: Config
            clusters:
              - name: refused
                cluster:
                  server: http://127.0.0.1:1
            contexts:
              - name: refused
                context:
                  cluster: refused
                  user: tester
            current-context: refused
            users:
              - name: tester
                user:
                  token: not-a-credential
            """);
        return path;
    }
}

/// <summary>
/// An API server that answers <c>/version</c> and holds every other request open until it
/// is disposed: a cluster whose discovery is slow, with the slowness made unbounded.
/// </summary>
internal sealed class VersionOnlyServer : IAsyncDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;

    public VersionOnlyServer()
    {
        _listener.Start();
        _accepting = AcceptAsync();
    }

    private int Port => ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;

    public string Kubeconfig(bool insecureSkipTlsVerify = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "kubenimbus-app-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "kubeconfig.yaml");
        File.WriteAllText(path, $"""
            apiVersion: v1
            kind: Config
            clusters:
              - name: slow
                cluster:
                  server: http://127.0.0.1:{Port}
                  insecure-skip-tls-verify: {(insecureSkipTlsVerify ? "true" : "false")}
            contexts:
              - name: slow
                context:
                  cluster: slow
                  user: tester
            current-context: slow
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
                if (!path.StartsWith("/version", StringComparison.Ordinal))
                {
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                }

                const string body = """{"major":"1","minor":"31","gitVersion":"v1.31.0","platform":"linux/amd64"}""";
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

/// <summary>
/// ENG-29 and FEAT-57 at the shell: the no-kubeconfig card and the status bar under it
/// say different things (they used to render one property twice), and a picked folder's
/// new kubeconfig reaches the context list on the next focus without a manual rescan.
/// </summary>
[NotInParallel]
public class KubeconfigShellTests
{
    /// <summary>
    /// Redirected stores with no picked kubeconfig paths. The redirect alone is not enough
    /// while App's settings store fixes its path on first use (ENG-37): every test would
    /// share one settings.json, and a folder picked by one test would surface as contexts
    /// in the next — which is what failed these tests on CI in some orders.
    /// </summary>
    private static void FreshStores()
    {
        TestObjects.RedirectStores();
        App.Update(s => s with { KubeconfigPaths = [] });
    }

    [Test]
    public async Task The_card_keeps_the_diagnosis_and_the_status_bar_says_something_else()
    {
        FreshStores();
        var shell = new MainWindowViewModel();

        await shell.ReloadContextsCommand.ExecuteAsync(null);

        await Assert.That(shell.HasContexts).IsFalse();
        await Assert.That(shell.KubeconfigDiagnosis).IsEqualTo("No kubeconfig contexts found.");
        await Assert.That(shell.Status).IsNotEqualTo(shell.KubeconfigDiagnosis);
        await Assert.That(shell.Status).StartsWith("No clusters");
    }

    [Test]
    public async Task A_file_that_will_not_parse_puts_the_parser_in_the_card_and_not_in_the_status_bar()
    {
        FreshStores();
        var folder = Folder();
        await File.WriteAllTextAsync(Path.Combine(folder, "broken.yaml"), "apiVersion: v1\nkind: Config\nclusters: [ this is : not yaml\n");
        var shell = new MainWindowViewModel();

        await shell.AddKubeconfigFolderPathAsync(folder);
        await shell.ReloadContextsCommand.ExecuteAsync(null);

        await Assert.That(shell.HasContexts).IsFalse();
        await Assert.That(shell.KubeconfigDiagnosis).StartsWith("Failed to read kubeconfig.");
        await Assert.That(shell.KubeconfigDiagnosis).Contains("broken.yaml");
        await Assert.That(shell.Status).IsEqualTo("No clusters: the kubeconfig could not be read.");
    }

    [Test]
    public async Task A_kubeconfig_dropped_into_a_picked_folder_is_found_on_the_next_focus()
    {
        FreshStores();
        var folder = Folder();
        var shell = new MainWindowViewModel();
        await shell.AddKubeconfigFolderPathAsync(folder);
        await Assert.That(shell.AvailableContexts.Count).IsEqualTo(0);

        await File.WriteAllTextAsync(Path.Combine(folder, "team.yaml"), """
            apiVersion: v1
            kind: Config
            clusters:
            - name: team
              cluster:
                server: http://127.0.0.1:1
            contexts:
            - name: team-context
              context:
                cluster: team
                user: team
            users:
            - name: team
              user:
                token: not-a-credential
            """);
        await shell.RescanIfChangedAsync();

        await Assert.That(shell.AvailableContexts.Select(c => c.Name)).Contains("team-context");
        await Assert.That(shell.HasContexts).IsTrue();

        // What is kept is the folder's path — not the file in it, and nothing read from it
        // (hard rule 4).
        var picked = App.LoadSettings().KubeconfigPaths;
        await Assert.That(picked).Contains(folder);
        await Assert.That(picked.Any(p => p.EndsWith("team.yaml", StringComparison.Ordinal))).IsFalse();
    }

    private static string Folder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kubenimbus-app-tests", Guid.NewGuid().ToString("n"), "kubeconfigs");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
