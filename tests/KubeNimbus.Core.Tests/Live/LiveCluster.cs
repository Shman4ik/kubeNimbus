using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using k8s;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// Shared plumbing for the live-cluster verification tests (the <c>*LiveTests</c> classes
/// in this folder). They close VER rows that were argued from the wire format and pinned
/// against loopback stand-ins, by driving the same Core methods against a real API server.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every mutation happens inside one namespace these tests create and delete
/// themselves.</b> The sandbox is a shared cluster — other test runs, and people, use it
/// at the same time — so nothing here may change an object it did not create. Reading
/// the rest of the cluster (the demo namespaces, the CRD catalog, the node) is fine and
/// several tests do. The one exception is named where it happens: cordoning the
/// sandbox's one node, undone in a <c>finally</c> within about a second (see
/// <c>NodeOperationsLiveTests</c>).
/// </para>
/// <para>
/// <b>Object names carry a per-run suffix</b>, because a namespace left behind by a run
/// that was killed is reused rather than waited on, and a fixed name would then collide
/// with its own leftover.
/// </para>
/// <para>
/// <b>They skip, never pass, with no cluster</b> — through
/// <see cref="SandboxCluster.TryGetContextAsync"/>, which calls <c>Skip.Test</c>.
/// </para>
/// </remarks>
internal static class LiveCluster
{
    /// <summary>The one namespace these tests may mutate.</summary>
    public const string Namespace = "bundle-f";

    /// <summary>The field manager every apply in these tests uses, so their ownership is recognizable.</summary>
    public const string FieldManager = "kubenimbus-live-tests";

    /// <summary>
    /// The image every test pod runs. It is already on the sandbox's node (the demo
    /// manifests use it), and <c>IfNotPresent</c> keeps a run from depending on a
    /// registry the machine may not reach.
    /// </summary>
    public const string Image = "busybox:1.36";

    /// <summary>Distinguishes this run's objects from any a killed run left behind.</summary>
    public static readonly string RunId = Guid.NewGuid().ToString("N")[..6];

    private static readonly SemaphoreSlim NamespaceGate = new(1, 1);
    private static bool _namespaceReady;
    private static ClusterContext? _context;

    /// <summary>Discovery cache for these tests, so a run never writes the developer's own app-data cache.</summary>
    private static readonly string DiscoveryCacheDirectory =
        Path.Combine(Path.GetTempPath(), "kubenimbus-live-tests", "discovery");

    /// <summary>
    /// A client for the sandbox with <see cref="Namespace"/> existing and ready — or a
    /// skipped test when there is no sandbox.
    /// </summary>
    public static async Task<ClusterClient> ConnectAsync(CancellationToken ct)
    {
        var context = await SandboxCluster.TryGetContextAsync();

        // TryGetContextAsync skips (throws) rather than returning null; this is the belt.
        if (context is null)
        {
            Skip.Test("no sandbox cluster");
            throw new UnreachableException();
        }

        _context = context;
        var client = await ClusterClient.ConnectAsync(context, ct);
        client.DiscoveryCacheDirectory = DiscoveryCacheDirectory;
        await client.GetServerVersionAsync(ct);
        await EnsureNamespaceAsync(client, ct);
        return client;
    }

    /// <summary>
    /// Creates the namespace once per run. A namespace still terminating from a previous
    /// run is waited out (creating into one is refused); an active one is reused.
    /// </summary>
    private static async Task EnsureNamespaceAsync(ClusterClient client, CancellationToken ct)
    {
        if (_namespaceReady)
        {
            return;
        }

        await NamespaceGate.WaitAsync(ct);
        try
        {
            if (_namespaceReady)
            {
                return;
            }

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(120);
            while (true)
            {
                var existing = await client.ReadResourceAsync(ResourceDescriptor.Namespaces, null, Namespace, ct);
                if (existing is null)
                {
                    await client.ApplyYamlAsync(
                        ResourceDescriptor.Namespaces, null, Namespace,
                        $$"""
                        apiVersion: v1
                        kind: Namespace
                        metadata:
                          name: {{Namespace}}
                          labels:
                            app.kubernetes.io/part-of: kubenimbus-live-tests
                        """,
                        FieldManager, cancellationToken: ct);
                    break;
                }

                if (Str(existing.Raw, "status", "phase") != "Terminating")
                {
                    break;
                }

                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new TimeoutException($"namespace {Namespace} is still terminating from a previous run");
                }

                await Task.Delay(1000, ct);
            }

            // A pod cannot be created until the namespace's default ServiceAccount exists.
            await WaitUntilAsync(
                async () => await client.ReadResourceAsync(ServiceAccounts, Namespace, "default", ct) is not null,
                TimeSpan.FromSeconds(60), "the namespace's default ServiceAccount", ct);

            _namespaceReady = true;
        }
        finally
        {
            NamespaceGate.Release();
        }
    }

    /// <summary>
    /// Deletes the namespace after the last test, when this run created or reused it.
    /// Not awaited to completion: the next run waits out a Terminating namespace itself.
    /// </summary>
    public static async Task DeleteNamespaceAsync()
    {
        if (!_namespaceReady || _context is null)
        {
            return;
        }

        using var client = await ClusterClient.ConnectAsync(_context);
        await client.DeleteResourceAsync(ResourceDescriptor.Namespaces, null, Namespace);
    }

    // ── Descriptors ──────────────────────────────────────────────────────────────────
    // Hand-built rather than discovered for the kinds every conformant server serves at
    // these versions, so a test that is about a Deployment does not also depend on the
    // discovery walk. Discovery is still used where the test is *about* discovery-driven
    // behaviour (subresources, the events.k8s.io group).

    public static readonly ResourceDescriptor Deployments = new(
        "apps", "v1", "Deployment", "deployments", "deployment", true, ["deploy"], ["all"])
    {
        Subresources = ["scale", "status"],
    };

    public static readonly ResourceDescriptor ReplicaSets = new(
        "apps", "v1", "ReplicaSet", "replicasets", "replicaset", true, ["rs"], ["all"]);

    public static readonly ResourceDescriptor DaemonSets = new(
        "apps", "v1", "DaemonSet", "daemonsets", "daemonset", true, ["ds"], ["all"]);

    public static readonly ResourceDescriptor PodDisruptionBudgets = new(
        "policy", "v1", "PodDisruptionBudget", "poddisruptionbudgets", "poddisruptionbudget", true, ["pdb"], []);

    public static readonly ResourceDescriptor ServiceAccounts = new(
        "", "v1", "ServiceAccount", "serviceaccounts", "serviceaccount", true, ["sa"], []);

    public static readonly ResourceDescriptor Nodes = new(
        "", "v1", "Node", "nodes", "node", false, ["no"], []);

    public static readonly ResourceDescriptor Widgets = new(
        "shop.kubenimbus.io", "v1", "Widget", "widgets", "widget", true, ["wdg"], []);

    public static readonly ResourceDescriptor FactoryWidgets = new(
        "factory.kubenimbus.io", "v1beta1", "Widget", "widgets", "widget", true, ["fwdg"], []);

    public static readonly ResourceDescriptor Backups = new(
        "demo.kubenimbus.io", "v1", "Backup", "backups", "backup", false, ["bkp"], []);

    public static readonly ResourceDescriptor EventsV1 = new(
        "events.k8s.io", "v1", "Event", "events", "event", true, ["ev"], []);

    // ── Objects ──────────────────────────────────────────────────────────────────────

    /// <summary>A per-run object name: <c>{prefix}-{run id}</c>.</summary>
    public static string Named(string prefix) => $"{prefix}-{RunId}";

    /// <summary>Server-side applies a manifest into <see cref="Namespace"/> and returns what the server stored.</summary>
    public static Task<DynamicResource> ApplyAsync(
        ClusterClient client, ResourceDescriptor descriptor, string name, string yaml, CancellationToken ct) =>
        client.ApplyYamlAsync(descriptor, descriptor.Namespaced ? Namespace : null, name, yaml, FieldManager, cancellationToken: ct);

    /// <summary>
    /// A Deployment of <see cref="Image"/> running <paramref name="script"/>, with a
    /// readiness probe that passes at once and a two-second grace period so a rollout or
    /// a delete in a test takes seconds rather than the default thirty.
    /// </summary>
    /// <param name="selector">
    /// The body of <c>spec.selector</c> as YAML at column 0 (<c>matchLabels:</c> …); defaults
    /// to <c>app: name</c>.
    /// </param>
    /// <param name="podLabels">Extra pod labels, as <c>key: value</c> lines at column 0.</param>
    /// <param name="strategy">The body of <c>spec.strategy</c> as YAML at column 0, or empty for the default.</param>
    public static string DeploymentYaml(
        string name, int replicas, string script, string selector = "", string podLabels = "", string strategy = "")
    {
        selector = selector.Length == 0 ? $"matchLabels:\n  app: {name}" : selector;
        var yaml = new StringBuilder();
        yaml.Append($"""
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: {name}
              namespace: {Namespace}
            spec:
              replicas: {replicas}
              selector:

            """);
        yaml.Append(Indent(selector, 4)).Append('\n');
        if (strategy.Length > 0)
        {
            yaml.Append("  strategy:\n").Append(Indent(strategy, 4)).Append('\n');
        }

        yaml.Append($"""
              template:
                metadata:
                  labels:
                    app: {name}

            """);
        if (podLabels.Length > 0)
        {
            yaml.Append(Indent(podLabels, 8)).Append('\n');
        }

        yaml.Append($"""
                spec:
                  terminationGracePeriodSeconds: 2
                  containers:
                    - name: app
                      image: {Image}
                      imagePullPolicy: IfNotPresent
                      command: ["sh", "-c", {JsonSerializer.Serialize(script)}]
                      readinessProbe:
                        exec:
                          command: ["true"]
                        periodSeconds: 1

            """);
        return yaml.ToString();
    }

    /// <summary>
    /// A shell loop that exits promptly on SIGTERM. busybox <c>sh</c> as PID 1 ignores
    /// TERM by default, which would make every pod in these tests sit out its whole grace
    /// period.
    /// </summary>
    public static string Loop(string body, string interval = "1") =>
        $"trap 'exit 0' TERM; while true; do {body}; sleep {interval} & wait $!; done";

    private static string Indent(string text, int spaces)
    {
        var pad = new string(' ', spaces);
        return string.Join('\n', text.Split('\n').Select(l => l.Length == 0 ? l : pad + l));
    }

    /// <summary>The pods in <see cref="Namespace"/> carrying <c>app=<paramref name="app"/></c>.</summary>
    public static async Task<IReadOnlyList<DynamicResource>> PodsOfAsync(ClusterClient client, string app, CancellationToken ct)
    {
        var selector = new LabelSelector([new LabelRequirement("app", LabelOperator.In, [app])]);
        return await client.ListResourceOnceAsync(ResourceDescriptor.Pods, Namespace, cancellationToken: ct, labelSelector: selector);
    }

    public static bool IsReady(DynamicResource pod) =>
        PodDetails.Conditions(pod).Any(c => c.Type == "Ready" && c.Status == "True");

    public static bool IsTerminating(DynamicResource pod) =>
        pod.Raw.TryGetProperty("metadata", out var m) && m.TryGetProperty("deletionTimestamp", out var d)
        && d.ValueKind == JsonValueKind.String;

    /// <summary>Waits until every pod of <paramref name="app"/> is Ready and there are exactly <paramref name="count"/> of them, none terminating.</summary>
    public static Task WaitForReadyPodsAsync(ClusterClient client, string app, int count, CancellationToken ct, int seconds = 90) =>
        WaitUntilAsync(
            async () =>
            {
                var pods = await PodsOfAsync(client, app, ct);
                return pods.Count == count && pods.All(p => IsReady(p) && !IsTerminating(p));
            },
            TimeSpan.FromSeconds(seconds), $"{count} ready pod(s) of {app}", ct);

    /// <summary>Polls <paramref name="condition"/> until it holds, or fails the test naming what it was waiting for.</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string what, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            if (await condition())
            {
                return;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out after {timeout.TotalSeconds:0}s waiting for {what}");
            }

            await Task.Delay(500, ct);
        }
    }

    /// <summary>
    /// The API server's own <c>Table</c> rendering of a list — exactly what <c>kubectl
    /// get</c> prints, because kubectl asks for this and prints the cells it is given.
    /// That is what makes it the reference for "matches kubectl, column for column"
    /// without a kubectl binary on the test machine.
    /// </summary>
    public static async Task<ServerTable> GetTableAsync(ClusterClient client, string path, CancellationToken ct)
    {
        using var response = await client.SendRequestAsync(
            HttpMethod.Get, path, content: null, HttpCompletionOption.ResponseContentRead, ct,
            accept: "application/json;as=Table;v=v1;g=meta.k8s.io");
        await ClusterClient.EnsureSuccessAsync(response, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return ServerTable.Parse(body);
    }

    /// <summary>
    /// A client authenticated as a ServiceAccount of <see cref="Namespace"/> whose only
    /// rights are the ones <paramref name="rules"/> grants — the narrow-RBAC user the
    /// backlog rows call "an impersonated user". A real token from the TokenRequest API
    /// rather than an <c>Impersonate-User</c> header: the app has no impersonation
    /// support to exercise, and a real identity is what the 403s it surfaces come from in
    /// practice. The kubeconfig is a temp file this test wrote for itself, holding a
    /// ten-minute token for a ServiceAccount that is deleted with the namespace.
    /// </summary>
    public static async Task<NarrowUser> CreateNarrowUserAsync(
        ClusterClient admin, string name, string rules, CancellationToken ct)
    {
        await ApplyAsync(admin, ServiceAccounts, name, $$"""
            apiVersion: v1
            kind: ServiceAccount
            metadata:
              name: {{name}}
              namespace: {{Namespace}}
            """, ct);
        await ApplyAsync(admin, ResourceDescriptor.Roles, name, $$"""
            apiVersion: rbac.authorization.k8s.io/v1
            kind: Role
            metadata:
              name: {{name}}
              namespace: {{Namespace}}
            rules:
            {{rules}}
            """, ct);
        await ApplyAsync(admin, ResourceDescriptor.RoleBindings, name, $$"""
            apiVersion: rbac.authorization.k8s.io/v1
            kind: RoleBinding
            metadata:
              name: {{name}}
              namespace: {{Namespace}}
            subjects:
              - kind: ServiceAccount
                name: {{name}}
                namespace: {{Namespace}}
            roleRef:
              apiGroup: rbac.authorization.k8s.io
              kind: Role
              name: {{name}}
            """, ct);

        using var tokenRequest = new StringContent(
            """{"apiVersion":"authentication.k8s.io/v1","kind":"TokenRequest","spec":{"expirationSeconds":600}}""",
            Encoding.UTF8, "application/json");
        using var response = await admin.SendRequestAsync(
            HttpMethod.Post,
            $"api/v1/namespaces/{Namespace}/serviceaccounts/{name}/token",
            tokenRequest, HttpCompletionOption.ResponseContentRead, ct);
        await ClusterClient.EnsureSuccessAsync(response, ct);
        using var tokenDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var token = tokenDoc.RootElement.GetProperty("status").GetProperty("token").GetString()!;

        var sandbox = (await KubeconfigReader.LoadAsync(admin.Context.KubeconfigPath, ct)).Configuration;
        var contextEntry = sandbox.Contexts.First(c => c.Name == admin.Context.Name);
        var cluster = sandbox.Clusters.First(c => c.Name == contextEntry.ContextDetails.Cluster).ClusterEndpoint;

        var path = Path.Combine(Path.GetTempPath(), $"kubenimbus-live-{name}.yaml");
        await File.WriteAllTextAsync(path, $$"""
            apiVersion: v1
            kind: Config
            clusters:
              - name: sandbox
                cluster:
                  server: {{cluster.Server}}
            {{(cluster.CertificateAuthorityData is { Length: > 0 } ca
                ? $"      certificate-authority-data: {ca}"
                : "      insecure-skip-tls-verify: true")}}
            users:
              - name: {{name}}
                user:
                  token: {{token}}
            contexts:
              - name: {{name}}
                context:
                  cluster: sandbox
                  user: {{name}}
                  namespace: {{Namespace}}
            current-context: {{name}}
            """, ct);

        var context = new ClusterContext(name, "sandbox", Namespace, name, path);
        var client = await ClusterClient.ConnectAsync(context, ct);
        return new NarrowUser(client, path, $"system:serviceaccount:{Namespace}:{name}");
    }

    private static string Str(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return "";
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() ?? "" : "";
    }
}

/// <summary>The after-run hook that removes <see cref="LiveCluster.Namespace"/>.</summary>
public static class LiveClusterHooks
{
    [After(HookType.Assembly)]
    public static Task DeleteLiveNamespaceAsync() => LiveCluster.DeleteNamespaceAsync();
}

/// <summary>A client for a narrow-RBAC ServiceAccount, and the temp kubeconfig behind it.</summary>
internal sealed record NarrowUser(ClusterClient Client, string KubeconfigPath, string UserName) : IDisposable
{
    public void Dispose()
    {
        Client.Dispose();
        try
        {
            File.Delete(KubeconfigPath);
        }
        catch (IOException)
        {
            // A temp file; a leftover is harmless and holds a token that expires in minutes.
        }
    }
}

/// <summary>A parsed <c>meta.k8s.io/v1 Table</c>: its column definitions and rows of raw cells.</summary>
internal sealed class ServerTable : IDisposable
{
    private readonly JsonDocument _document;

    private ServerTable(
        JsonDocument document,
        IReadOnlyList<(string Name, string Type, int Priority)> columns,
        IReadOnlyList<(string Key, JsonElement[] Cells)> rows)
    {
        _document = document;
        Columns = columns;
        Rows = rows;
    }

    public IReadOnlyList<(string Name, string Type, int Priority)> Columns { get; }

    /// <summary>Each row's cells, keyed <c>namespace/name</c> from the row's own object metadata.</summary>
    public IReadOnlyList<(string Key, JsonElement[] Cells)> Rows { get; }

    public int IndexOf(string column) =>
        Columns.Select((c, i) => (c, i)).First(x => x.c.Name == column).i;

    /// <summary>The cells of the row for <paramref name="key"/> (<c>namespace/name</c>, or <c>/name</c> when cluster-scoped).</summary>
    public JsonElement[] Row(string key) => Rows.First(r => r.Key == key).Cells;

    public static ServerTable Parse(string body)
    {
        var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var columns = root.GetProperty("columnDefinitions").EnumerateArray()
            .Select(c => (
                c.GetProperty("name").GetString() ?? "",
                c.GetProperty("type").GetString() ?? "",
                c.TryGetProperty("priority", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0))
            .ToList();
        var rows = new List<(string, JsonElement[])>();
        if (root.TryGetProperty("rows", out var rowsElement) && rowsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rowsElement.EnumerateArray())
            {
                var cells = row.GetProperty("cells").EnumerateArray().ToArray();
                var key = row.TryGetProperty("object", out var obj) && obj.TryGetProperty("metadata", out var meta)
                    ? $"{(meta.TryGetProperty("namespace", out var ns) ? ns.GetString() : "")}/{meta.GetProperty("name").GetString()}"
                    : $"?/{cells[0].GetString()}";
                rows.Add((key, cells));
            }
        }

        return new ServerTable(document, columns, rows);
    }

    /// <summary>A cell as kubectl prints it: null as empty, a string as itself, anything else as its JSON text.</summary>
    public static string Text(JsonElement cell) => cell.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.String => cell.GetString() ?? "",
        _ => cell.GetRawText(),
    };

    public void Dispose() => _document.Dispose();
}
