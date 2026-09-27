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
