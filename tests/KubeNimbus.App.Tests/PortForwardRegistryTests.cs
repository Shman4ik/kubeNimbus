using System.Net.Sockets;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// FEAT-7: a forward outlives its dock tab, is listed in the window's registry from Start to
/// Stop, can be reopened there, and stops — saying so — when its cluster tab closes. The
/// forwards here are real: <c>StartAsync</c> binds a loopback listener (the offline client
/// is only reached when a connection is accepted), so "still forwarding" is checked as a
/// local port that still accepts a connection, not as a flag.
/// </summary>
public class PortForwardRegistryTests
{
    private static (ClusterTabViewModel Tab, PortForwardRegistry Registry, PortForwardTabViewModel Pane) Docked()
    {
        var tab = TestObjects.Tab();
        var registry = new PortForwardRegistry();
        tab.PortForwards = registry;
        var pane = new PortForwardTabViewModel(TestObjects.OfflineClient(), "payments", "shop-web-7f9c", 8080)
        {
            Owner = tab,
            Registry = registry,
            ClusterLabel = "test-cluster",
        };
        tab.ShowForward(pane);
        return (tab, registry, pane);
    }

    private static async Task<bool> AcceptsAsync(int port)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(5));
            return client.Connected;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    [Test]
    public async Task Closing_a_running_forwards_tab_keeps_it_listening_and_listed()
    {
        var (tab, registry, pane) = Docked();
        await pane.StartCommand.ExecuteAsync(null);
        await Assert.That(pane.IsRunning).IsTrue();
        await Assert.That(registry.Forwards).Contains(pane);
        await Assert.That(pane.KeepsRunningHint).IsNotNull();

        await tab.CloseInspectorTabCommand.ExecuteAsync(pane);

        await Assert.That(tab.InspectorTabs).DoesNotContain(pane);
        await Assert.That(pane.IsRunning).IsTrue();
        await Assert.That(registry.Count).IsEqualTo(1);
        await Assert.That(registry.StatusText).IsEqualTo("1 port-forward");
        await Assert.That(await AcceptsAsync(pane.LocalPort)).IsTrue();

        await pane.StopCommand.ExecuteAsync(null);
        await Assert.That(registry.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_closed_forward_reopens_in_the_tab_that_started_it()
    {
        var (tab, registry, pane) = Docked();
        ClusterTabViewModel? brought = null;
        registry.SelectTab = t => brought = t;
        await pane.StartCommand.ExecuteAsync(null);
        await tab.CloseInspectorTabCommand.ExecuteAsync(pane);

        registry.Reopen(pane);

        await Assert.That(brought).IsSameReferenceAs(tab);
        await Assert.That(tab.InspectorTabs).Contains(pane);
        await Assert.That(tab.SelectedInspectorTab).IsSameReferenceAs(pane);
        await Assert.That(pane.IsRunning).IsTrue();
        await pane.StopCommand.ExecuteAsync(null);
    }

    [Test]
    public async Task Closing_the_cluster_tab_stops_its_forwards_and_says_so()
    {
        var (tab, registry, pane) = Docked();
        await pane.StartCommand.ExecuteAsync(null);
        var port = pane.LocalPort;
        await tab.CloseInspectorTabCommand.ExecuteAsync(pane);

        var stopped = await registry.StopForClusterAsync(tab);

        await Assert.That(stopped).IsEqualTo(1);
        await Assert.That(pane.IsRunning).IsFalse();
        await Assert.That(registry.Count).IsEqualTo(0);
        await Assert.That(registry.Notice).IsEqualTo("Stopped 1 port-forward on test-cluster — its tab was closed.");
        await Assert.That(registry.HasSomethingToShow).IsTrue();
        await Assert.That(pane.StatusMessage).IsEqualTo("Stopped: the test-cluster tab was closed.");
        await Assert.That(await AcceptsAsync(port)).IsFalse();
    }

    [Test]
    public async Task The_forwards_list_stops_a_forward_on_the_click()
    {
        var (tab, registry, pane) = Docked();
        await pane.StartCommand.ExecuteAsync(null);
        tab.OpenPortForwardsCommand.Execute(null);
        var list = (PortForwardsTabViewModel)tab.SelectedInspectorTab!;

        await list.StopCommand.ExecuteAsync(pane);

        await Assert.That(pane.IsRunning).IsFalse();
        await Assert.That(list.Registry.IsEmpty).IsTrue();
    }

    /// <summary>
    /// The stop is said even while another cluster's forward still runs: the status bar line
    /// carries the notice beside the count, and so does its tooltip.
    /// </summary>
    [Test]
    public async Task A_stop_notice_shows_beside_the_count_of_forwards_still_running()
    {
        var (tab, registry, pane) = Docked();
        var other = new ClusterTabViewModel(new ClusterContext("other-cluster", "other", "default", "u", "/nonexistent/k.yaml"))
        {
            PortForwards = registry,
        };
        var survivor = new PortForwardTabViewModel(TestObjects.OfflineClient(), "shop", "web-1", 8080)
        {
            Owner = other,
            Registry = registry,
            ClusterLabel = "other-cluster",
        };
        await pane.StartCommand.ExecuteAsync(null);
        await survivor.StartCommand.ExecuteAsync(null);
        var raised = new List<string?>();
        registry.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await registry.StopForClusterAsync(tab);

        await Assert.That(registry.StatusText)
            .IsEqualTo("1 port-forward · Stopped 1 port-forward on test-cluster — its tab was closed.");
        await Assert.That(registry.Summary).Contains("Stopped 1 port-forward on test-cluster");
        await Assert.That(registry.Summary).Contains("shop/web-1:8080");
        await Assert.That(raised).Contains(nameof(PortForwardRegistry.Summary));
        await survivor.StopCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// A Service Start resolves over the network before it binds. A cluster tab closed in that
    /// window cancels it, and the forward never binds or registers on the disposed client.
    /// The "API server" here accepts the connection and never answers, so the Start is held
    /// in flight until something cancels it.
    /// </summary>
    [Test]
    [Timeout(30_000)]
    public async Task Closing_the_cluster_tab_cancels_a_start_still_in_flight(CancellationToken ct)
    {
        var silent = new TcpListener(System.Net.IPAddress.Loopback, 0);
        silent.Start();
        var held = new List<TcpClient>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    held.Add(await silent.AcceptTcpClientAsync(ct));
                }
            }
            catch (Exception)
            {
                // listener stopped
            }
        }, ct);

        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "kubenimbus-app-tests", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "kubeconfig.yaml");
            await File.WriteAllTextAsync(path, $$"""
                apiVersion: v1
                kind: Config
                clusters:
                  - name: silent
                    cluster:
                      server: http://127.0.0.1:{{((System.Net.IPEndPoint)silent.LocalEndpoint).Port}}
                contexts:
                  - name: silent
                    context:
                      cluster: silent
                      user: silent
                current-context: silent
                users:
                  - name: silent
                    user:
                      token: not-a-credential
                """, ct);
            using var client = ClusterClient.Connect(new ClusterContext("silent", "silent", null, "silent", path));
            var tab = TestObjects.Tab();
            var registry = new PortForwardRegistry();
            var pane = PortForwardTabViewModel.ForService(
                client, "shop", "shop-api", [new Core.Networking.ServicePortInfo("http", 80, "web", 0, "TCP", "")]);
            pane.Owner = tab;
            pane.Registry = registry;

            var start = pane.StartCommand.ExecuteAsync(null);
            await Task.Delay(300, ct);
            await Assert.That(start.IsCompleted).IsFalse();

            await registry.StopForClusterAsync(tab);
            await start.WaitAsync(TimeSpan.FromSeconds(10), ct);

            await Assert.That(pane.IsRunning).IsFalse();
            await Assert.That(registry.IsEmpty).IsTrue();
            await Assert.That(pane.StatusMessage).IsEqualTo("Stopped: the test-cluster tab was closed.");
            await Assert.That(registry.Notice).IsEqualTo("Stopped 1 port-forward on test-cluster — its tab was closed.");
        }
        finally
        {
            silent.Stop();
            foreach (var c in held)
            {
                c.Dispose();
            }
        }
    }

    /// <summary>With no registry to list it (a pane built alone), closing still stops it — nothing would show it otherwise.</summary>
    [Test]
    public async Task Without_a_registry_closing_the_pane_stops_the_forward()
    {
        var pane = new PortForwardTabViewModel(TestObjects.OfflineClient(), "payments", "shop-web-7f9c", 8080);
        await pane.StartCommand.ExecuteAsync(null);
        var port = pane.LocalPort;

        await pane.OnClosingAsync();

        await Assert.That(pane.IsRunning).IsFalse();
        await Assert.That(await AcceptsAsync(port)).IsFalse();
    }

    /// <summary>The shell wires it: a tab entering the strip carries the registry, and closing it stops its forwards first.</summary>
    [Test]
    [NotInParallel]
    public async Task The_shell_closing_a_cluster_tab_stops_the_forwards_on_it()
    {
        TestObjects.RedirectStores();
        using var shell = new MainWindowViewModel();
        await shell.Initialization;
        var tab = new ClusterTabViewModel(TestObjects.Context);
        shell.Tabs.Add(tab);
        await Assert.That(tab.PortForwards).IsSameReferenceAs(shell.PortForwards);

        var pane = new PortForwardTabViewModel(TestObjects.OfflineClient(), "payments", "shop-web-7f9c", 8080)
        {
            Owner = tab,
            Registry = shell.PortForwards,
        };
        await pane.StartCommand.ExecuteAsync(null);
        await Assert.That(shell.PortForwards.Count).IsEqualTo(1);

        await shell.CloseTabCommand.ExecuteAsync(tab);

        await Assert.That(pane.IsRunning).IsFalse();
        await Assert.That(shell.PortForwards.Notice).IsNotNull();
    }
}
