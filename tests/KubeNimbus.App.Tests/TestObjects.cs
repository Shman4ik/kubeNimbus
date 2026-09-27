using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;
using KubeNimbus.Core.Settings;

namespace KubeNimbus.App.Tests;

/// <summary>
/// Minimal real objects for the view-model tests: a <see cref="DynamicResource"/> built
/// from actual pod JSON (not a stub type), a <see cref="ResourceEvent{T}"/> around it,
/// and a tab to apply them to.
/// </summary>
internal static class TestObjects
{
    /// <summary>
    /// A context that is neither the demo cluster nor connectable — <c>Client</c> stays
    /// null, so nothing in these tests can start a watch, a metrics poll or a socket.
    /// The kubeconfig path is never opened: only <c>ConnectAsync</c> reads it.
    /// </summary>
    public static ClusterContext Context { get; } =
        new("test-cluster", "test-cluster", "default", "tester", "/nonexistent/kubeconfig.yaml");

    /// <summary>
    /// Runs <see cref="RedirectStores"/> once when the test assembly loads, before any test
    /// can touch a store. What this buys is a guarantee rather than a convention: a test
    /// that builds a view model without calling <see cref="RedirectStores"/> first — easy
    /// to write, and nothing would notice — still cannot reach the real
    /// <c>settings.json</c> or <c>workspace.json</c> of whoever is running the suite.
    /// </summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RedirectStoresAtLoad() => RedirectStores();

    /// <summary>
    /// Points <c>settings.json</c> and <c>workspace.json</c> at a fresh, empty temp
    /// directory. The App layer reads settings on paths these tests touch (sidebar section
    /// expansion, the metrics poll interval, "open logs maximized"), and a test run must
    /// not read — still less write — the files belonging to whoever is running it. Same
    /// reason the screenshot harness sets these.
    ///
    /// <para>
    /// What it guarantees, exactly (ENG-37), because the old comment promised more:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Never the developer's files.</b> Also true of a test that never calls this,
    /// through <see cref="RedirectStoresAtLoad"/>.</item>
    /// <item><b>A fresh file for the calling test</b> — every read and write after this call
    /// lands in the new directory, <c>App</c>'s settings included, since <c>App</c> no
    /// longer fixes its path on first use.</item>
    /// <item><b>Not isolation from a test running in parallel.</b> Both overrides are
    /// process-wide statics, so another test calling this between your write and your read
    /// moves you to <em>its</em> directory. A test that writes a setting or the workspace
    /// and then relies on it is <c>[NotInParallel]</c> — <c>RowLogsTests</c>,
    /// <c>ClusterTabSortTests</c> and <c>ClusterTabNamespacePickerTests</c> are the
    /// pattern.</item>
    /// </list>
    /// </summary>
    public static void RedirectStores()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kubenimbus-app-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        AppSettingsStore.DirectoryOverride = directory;
        WorkspaceStore.DirectoryOverride = directory;

        // The shell reads the kubeconfig chain on construction and, with no saved tabs,
        // opens one on the current context — against the real ~/.kube/config that was a
        // connect to the developer's own cluster from inside a unit test, and the async
        // restore it started made the shell-mode tests order-dependent.
        Kubeconfig.EnvironmentSearchOverride = [];
    }

    public static ClusterTabViewModel Tab()
    {
        RedirectStores();
        return new ClusterTabViewModel(Context);
    }

    /// <summary>
    /// A real <c>ClusterClient</c> pointed at <c>https://127.0.0.1:1</c>, where nothing
    /// listens — for the paths that behave differently once a client exists (a
    /// client-less view model is the demo cluster, which refuses every mutation before
    /// the logic under test is reached). Any request it makes fails fast; the tests that
    /// use it make none. Built with the synchronous <c>Connect</c>, which is fine off the
    /// UI thread and is what that overload is kept for.
    /// </summary>
    public static ClusterClient OfflineClient()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kubenimbus-app-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "kubeconfig.yaml");
        File.WriteAllText(path, """
            apiVersion: v1
            kind: Config
            clusters:
              - name: offline
                cluster:
                  server: https://127.0.0.1:1
                  insecure-skip-tls-verify: true
            contexts:
              - name: offline
                context:
                  cluster: offline
                  user: offline
            current-context: offline
            users:
              - name: offline
                user:
                  token: not-a-credential
            """);
        return ClusterClient.Connect(new ClusterContext("offline", "offline", null, "offline", path));
    }

    public static ResourceDescriptor PodDescriptor { get; } =
        new("", "v1", "Pod", "pods", "pod", Namespaced: true, ShortNames: ["po"], Categories: ["all"]);

    public static ResourceDescriptor NodeDescriptor { get; } =
        new("", "v1", "Node", "nodes", "node", Namespaced: false, ShortNames: ["no"], Categories: []);

    public static ResourceDescriptor ConfigMapDescriptor { get; } =
        new("", "v1", "ConfigMap", "configmaps", "configmap", Namespaced: true, ShortNames: ["cm"], Categories: []);

    /// <summary>
    /// A real pod object, parsed from JSON exactly as a watch frame would be. The
    /// optional arguments exist for the sort tests, which need rows that differ in the
    /// values the list's columns are ordered by — a restart count, a creation instant,
    /// a readiness — rather than only in their names.
    /// </summary>
    public static DynamicResource Pod(
        string @namespace,
        string name,
        string phase = "Running",
        int restarts = 0,
        string created = "2026-08-01T10:00:00Z",
        bool ready = true)
    {
        var json = $$"""
        {
          "apiVersion": "v1",
          "kind": "Pod",
          "metadata": {
            "name": "{{name}}",
            "namespace": "{{@namespace}}",
            "uid": "{{@namespace}}-{{name}}",
            "creationTimestamp": "{{created}}"
          },
          "spec": { "containers": [ { "name": "app" } ] },
          "status": {
            "phase": "{{phase}}",
            "containerStatuses": [
              { "name": "app", "ready": {{(ready ? "true" : "false")}}, "restartCount": {{restarts}}, "state": { "running": {} } }
            ]
          }
        }
        """;

        using var document = JsonDocument.Parse(json);
        return new DynamicResource(document.RootElement.Clone());
    }

    /// <summary>
    /// A ConfigMap — an object of a kind that has no Ready, no Status and no restart
    /// count, which is what makes it the "nothing in that column" case the sort has to
    /// place deliberately rather than as a zero.
    /// </summary>
    public static DynamicResource ConfigMap(string @namespace, string name)
    {
        var json = $$"""
        {
          "apiVersion": "v1",
          "kind": "ConfigMap",
          "metadata": {
            "name": "{{name}}",
            "namespace": "{{@namespace}}",
            "uid": "{{@namespace}}-{{name}}",
            "creationTimestamp": "2026-08-01T10:00:00Z"
          },
          "data": { "key": "value" }
        }
        """;

        using var document = JsonDocument.Parse(json);
        return new DynamicResource(document.RootElement.Clone());
    }

    public static ResourceEvent<DynamicResource> Added(DynamicResource resource) =>
        new(ResourceEventType.Added, resource);

    public static ResourceEvent<DynamicResource> Modified(DynamicResource resource) =>
        new(ResourceEventType.Modified, resource);

    public static ResourceEvent<DynamicResource> Deleted(DynamicResource resource) =>
        new(ResourceEventType.Deleted, resource);

    /// <summary>
    /// Names of the rows the list is currently rendering, joined in order. A string
    /// rather than a collection so the assertion is order-sensitive without depending on
    /// any assertion library's collection-ordering default, and so a failure prints both
    /// sequences side by side.
    /// </summary>
    public static string VisibleNames(this ClusterTabViewModel tab) =>
        string.Join(", ", tab.VisibleRows.Select(r => r.Name));

    /// <summary>Names of every row the watch knows about, joined in order.</summary>
    public static string RowNames(this ClusterTabViewModel tab) =>
        string.Join(", ", tab.Rows.Select(r => r.Name));
}
