using System.Net;
using System.Text;
using System.Text.Json;
using KubeNimbus.Core.Applications;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// The Argo CD actions against a <b>real</b> Argo CD (VER-15 #154, VER-60 #190, FEAT-74 #193):
/// a pinned release installed into a second, throwaway sandbox by
/// <c>scripts/argo-sandbox-up.ps1</c>, with applications in any namespace on and a Git server
/// inside the cluster. The ordinary sandbox has only a stand-in CRD and no controller, so
/// every test here skips there, and in CI.
///
/// <para>
/// Each test makes its own Applications (and AppProject, and destination namespaces) under a
/// name carrying this run's id, and deletes them in a <c>finally</c>. The sandbox is this
/// run's own, but the names keep two runs from meeting.
/// </para>
/// </summary>
[NotInParallel(nameof(ArgoLiveTests))]
public class ArgoLiveTests
{
    private const string ArgoNamespace = "argocd";
    private const string TenantNamespace = "argo-tenant";
    private const string Repo = "git://argo-git.argocd.svc.cluster.local:9418/demo.git";
    private const string FieldManager = "kubenimbus-argo-live-tests";

    private static readonly string RunId = Guid.NewGuid().ToString("N")[..6];

    private static readonly TimeSpan Reconcile = TimeSpan.FromSeconds(150);

    private static readonly ResourceDescriptor StatefulSets =
        new("apps", "v1", "StatefulSet", "statefulsets", "statefulset", Namespaced: true, ShortNames: [], Categories: []);

    private static readonly ResourceDescriptor Namespaces = ResourceDescriptor.Namespaces;

    private static string Named(string what) => $"kn-{RunId}-{what}";

    // ------------------------------------------------------------------ the gate

    /// <summary>
    /// The Argo sandbox's kubeconfig: <c>$KUBENIMBUS_ARGO_KUBECONFIG</c>, else
    /// <c>.sandbox/argo-kubeconfig.yaml</c> above the test binary (what the script writes).
    /// </summary>
    private static string? KubeconfigPath()
    {
        var env = Environment.GetEnvironmentVariable("KUBENIMBUS_ARGO_KUBECONFIG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        foreach (var start in (string[])[AppContext.BaseDirectory, Environment.GetEnvironmentVariable("KUBENIMBUS_REPO_ROOT") ?? ""])
        {
            for (var dir = start.Length == 0 ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, ".sandbox", "argo-kubeconfig.yaml");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private sealed record Argo(ClusterClient Client, ResourceDescriptor Applications, ResourceDescriptor Projects) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    /// <summary>A client for the Argo sandbox — or a skipped test when there is none, or it runs no real Argo CD.</summary>
    private static async Task<Argo> ConnectAsync(CancellationToken ct)
    {
        if (KubeconfigPath() is not { } path)
        {
            Skip.Test("no Argo sandbox kubeconfig (.sandbox/argo-kubeconfig.yaml; see scripts/argo-sandbox-up.ps1)");
            throw new InvalidOperationException();
        }

        var contexts = await Kubeconfig.LoadContextsAsync([path]);
        ClusterClient client;
        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromSeconds(5));
            client = await ClusterClient.ConnectAsync(contexts[0], probe.Token);
            client.DiscoveryCacheDirectory = Path.Combine(Path.GetTempPath(), "kubenimbus-argo-live-tests", "discovery");
            await client.GetServerVersionAsync(probe.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or k8s.Exceptions.KubeConfigException)
        {
            Skip.Test($"{path} does not lead to a cluster this machine can talk to ({ex.Message})");
            throw;
        }

        // A real installation, not the ordinary sandbox's stand-in CRD: the controller is there.
        if (await client.ReadResourceAsync(StatefulSets, ArgoNamespace, "argocd-application-controller", ct) is null)
        {
            client.Dispose();
            Skip.Test("the Argo sandbox runs no Argo CD controller (run scripts/argo-sandbox-up.ps1)");
        }

        var catalog = await client.GetResourceCatalogAsync(ct, forceRefresh: true);
        return new Argo(
            client,
            ArgoCd.ApplicationDescriptor(catalog)!,
            catalog.First(ArgoCd.IsProjectKind));
    }

    // ------------------------------------------------------------------ helpers

    private static Task CreateApplicationAsync(
        Argo argo, string name, string path, string revision, string destination,
        string project = "default", string @namespace = ArgoNamespace, string extraStatus = "") =>
        argo.Client.ApplyYamlAsync(argo.Applications, @namespace, name, $"""
            apiVersion: argoproj.io/v1alpha1
            kind: Application
            metadata:
              name: {name}
              namespace: {@namespace}
            spec:
              project: {project}
              source:
                repoURL: {Repo}
                path: {path}
                targetRevision: {revision}
              destination:
                server: https://kubernetes.default.svc
                namespace: {destination}
              syncPolicy:
                syncOptions: [CreateNamespace=true]
            {extraStatus}
            """, FieldManager);

    private static async Task<DynamicResource> ReadAsync(Argo argo, string name, string @namespace = ArgoNamespace) =>
        await argo.Client.ReadResourceAsync(argo.Applications, @namespace, name)
        ?? throw new InvalidOperationException($"Application {@namespace}/{name} is gone");

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

    private static async Task<string> WaitForPhaseAsync(Argo argo, string name, Func<string, string, bool> done, string what, CancellationToken ct)
    {
        var phase = "";
        await LiveCluster.WaitUntilAsync(async () =>
        {
            var raw = (await ReadAsync(argo, name)).Raw;
            phase = Str(raw, "status", "operationState", "phase");
            return done(phase, Str(raw, "status", "operationState", "message"));
        }, Reconcile, what, ct);
        return phase;
    }

    /// <summary>
    /// Syncs, then waits for an operation that started after the request to finish — the
    /// previous sync's Succeeded must not be read as this one's.
    /// </summary>
    private static async Task<string> SyncAndWaitAsync(Argo argo, string name, bool prune, CancellationToken ct)
    {
        // startedAt has one-second resolution and a second sync can start inside the first's
        // second, so a new history entry (one per successful sync) also counts as "new".
        static int History(JsonElement raw) =>
            raw.TryGetProperty("status", out var s) && s.TryGetProperty("history", out var h) && h.ValueKind == JsonValueKind.Array
                ? h.GetArrayLength() : 0;
        var start = (await ReadAsync(argo, name)).Raw;
        var before = Str(start, "status", "operationState", "startedAt");
        var historyBefore = History(start);
        await argo.Client.SyncArgoApplicationAsync(argo.Applications, ArgoNamespace, name, prune);
        var phase = "";
        var last = "";
        try
        {
            await LiveCluster.WaitUntilAsync(async () =>
            {
                var raw = (await ReadAsync(argo, name)).Raw;
                phase = Str(raw, "status", "operationState", "phase");
                last = $"phase {phase}, started {Str(raw, "status", "operationState", "startedAt")} (before {before}), "
                    + $"message \"{Str(raw, "status", "operationState", "message")}\", operation {(raw.TryGetProperty("operation", out var op) ? op.GetRawText() : "none")}";
                return (Str(raw, "status", "operationState", "startedAt") != before || History(raw) > historyBefore)
                    && phase is "Succeeded" or "Failed" or "Error"
                    && !raw.TryGetProperty("operation", out _);
            }, Reconcile, $"the sync (prune: {prune}) to finish", ct);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message}; last seen: {last}", ex);
        }

        return phase;
    }

    private static async Task<bool> ConfigMapExistsAsync(Argo argo, string @namespace, string name) =>
        await argo.Client.ReadResourceAsync(ResourceDescriptor.ConfigMaps, @namespace, name) is not null;

    private static async Task MergePatchAsync(Argo argo, string @namespace, string name, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/merge-patch+json");
        using var response = await argo.Client.SendRequestAsync(
            HttpMethod.Patch, argo.Applications.ItemPath(@namespace, name), content, HttpCompletionOption.ResponseContentRead, default);
        await ClusterClient.EnsureSuccessAsync(response, default);
    }

    private static async Task CleanUpAsync(Argo argo, IEnumerable<(string Namespace, string Name)> applications, params string[] namespaces)
    {
        foreach (var (ns, name) in applications)
        {
            try
            {
                await argo.Client.DeleteResourceAsync(argo.Applications, ns, name);
            }
            catch (KubernetesApiException)
            {
                // already gone
            }
        }

        foreach (var ns in namespaces)
        {
            try
            {
                await argo.Client.DeleteResourceAsync(Namespaces, null, ns);
            }
            catch (KubernetesApiException)
            {
                // already gone
            }
        }
    }

    // ------------------------------------------------------------------ VER-15 (#154)

    /// <summary>
    /// A sync from kubeNimbus is run by Argo's controller, records the user beside the tool as
    /// its initiator, and carries the Application's own sync options — without them the
    /// CreateNamespace=true destination was never created and the sync failed (found by this
    /// test's first run). Then the prune half: with the Application moved to the commit that
    /// deleted <c>removed.yaml</c>, a sync without prune leaves the ConfigMap and a sync with
    /// prune deletes it, keeping the one Git still declares.
    /// </summary>
    [Test]
    public async Task A_sync_runs_in_argo_names_its_initiator_and_prune_deletes_what_left_git(CancellationToken ct)
    {
        using var argo = await ConnectAsync(ct);
        var name = Named("sync");
        var destination = Named("sync-dest");
        try
        {
            await CreateApplicationAsync(argo, name, "app", "with-removed", destination);

            await argo.Client.SyncArgoApplicationAsync(argo.Applications, ArgoNamespace, name);
            var phase = await WaitForPhaseAsync(argo, name, (p, _) => p is "Succeeded" or "Failed" or "Error", "the first sync to finish", ct);

            var raw = (await ReadAsync(argo, name)).Raw;
            var username = await argo.Client.GetCurrentUsernameAsync(ct);
            await Assert.That(phase).IsEqualTo("Succeeded");
            await Assert.That(Str(raw, "status", "operationState", "operation", "initiatedBy", "username"))
                .IsEqualTo(ArgoCd.InitiatorFor(username));
            await Assert.That(await ConfigMapExistsAsync(argo, destination, "keep")).IsTrue();
            await Assert.That(await ConfigMapExistsAsync(argo, destination, "removed")).IsTrue();

            // removed.yaml leaves Git.
            await MergePatchAsync(argo, ArgoNamespace, name, """{"spec":{"source":{"targetRevision":"without-removed"}}}""");

            await Assert.That(await SyncAndWaitAsync(argo, name, prune: false, ct)).IsEqualTo("Succeeded");
            await Assert.That(await ConfigMapExistsAsync(argo, destination, "removed")).IsTrue();

            await Assert.That(await SyncAndWaitAsync(argo, name, prune: true, ct)).IsEqualTo("Succeeded");
            await LiveCluster.WaitUntilAsync(
                async () => !await ConfigMapExistsAsync(argo, destination, "removed"),
                Reconcile, "prune to delete the ConfigMap that left Git", ct);
            await Assert.That(await ConfigMapExistsAsync(argo, destination, "keep")).IsTrue();
        }
        finally
        {
            await CleanUpAsync(argo, [(ArgoNamespace, name)], destination);
        }
    }

    /// <summary>Argo's controller clears the refresh annotation once it has re-compared — the reason the strip says "requested".</summary>
    [Test]
    public async Task A_refresh_is_picked_up_and_its_annotation_cleared(CancellationToken ct)
    {
        using var argo = await ConnectAsync(ct);
        var name = Named("refresh");
        try
        {
            await CreateApplicationAsync(argo, name, "app", "with-removed", Named("refresh-dest"));
            await argo.Client.RefreshArgoApplicationAsync(argo.Applications, ArgoNamespace, name, hard: true);

            await LiveCluster.WaitUntilAsync(
                async () => Str((await ReadAsync(argo, name)).Raw, "metadata", "annotations", ArgoCd.RefreshAnnotation).Length == 0,
                Reconcile, "Argo to clear the refresh annotation", ct);
            await Assert.That(ArgoCd.ReadApplication(await ReadAsync(argo, name)).Sync).IsEqualTo(ArgoSyncState.OutOfSync);
        }
        finally
        {
            await CleanUpAsync(argo, [(ArgoNamespace, name)]);
        }
    }

    /// <summary>
    /// A sync from kubeNimbus is a manual sync, and a deny window with <c>manualSync: false</c>
    /// blocks it: the controller holds the operation and says why, and nothing is applied.
    /// </summary>
    [Test]
    public async Task A_deny_window_blocks_a_manual_sync(CancellationToken ct)
    {
        using var argo = await ConnectAsync(ct);
        var project = Named("windowed");
        var name = Named("windowed-app");
        var destination = Named("windowed-dest");
        try
        {
            await argo.Client.ApplyYamlAsync(argo.Projects, ArgoNamespace, project, $"""
                apiVersion: argoproj.io/v1alpha1
                kind: AppProject
                metadata:
                  name: {project}
                  namespace: {ArgoNamespace}
                spec:
                  sourceRepos: ['*']
                  destinations:
                    - server: https://kubernetes.default.svc
                      namespace: '*'
                  clusterResourceWhitelist:
                    - group: ''
                      kind: Namespace
                  syncWindows:
                    - kind: deny
                      schedule: '* * * * *'
                      duration: 1h
                      applications: ['*']
                      manualSync: false
                """, FieldManager);
            await CreateApplicationAsync(argo, name, "app", "with-removed", destination, project);

            await argo.Client.SyncArgoApplicationAsync(argo.Applications, ArgoNamespace, name);
            var message = "";
            await LiveCluster.WaitUntilAsync(async () =>
            {
                message = Str((await ReadAsync(argo, name)).Raw, "status", "operationState", "message");
                return message.Contains("sync window", StringComparison.OrdinalIgnoreCase);
            }, Reconcile, "the controller to report the sync window", ct);

            await Assert.That(message).Contains("blocked by sync window");
            await Assert.That(await ConfigMapExistsAsync(argo, destination, "keep")).IsFalse();
        }
        finally
        {
            await CleanUpAsync(argo, [(ArgoNamespace, name)], destination);
            try
            {
                await argo.Client.DeleteResourceAsync(argo.Projects, ArgoNamespace, project);
            }
            catch (KubernetesApiException)
            {
                // already gone
            }
        }
    }

    /// <summary>A user who may read Applications but not patch them gets the API server's own 403 sentence for sync and terminate.</summary>
    [Test]
    public async Task A_user_without_patch_gets_the_servers_403_for_sync_and_terminate(CancellationToken ct)
    {
        using var argo = await ConnectAsync(ct);
        var name = Named("rbac");
        var reader = Named("reader");
        string? kubeconfig = null;
        try
        {
            await CreateApplicationAsync(argo, name, "app", "with-removed", Named("rbac-dest"));
            kubeconfig = await CreateReaderAsync(argo.Client, reader, ct);
            var contexts = await Kubeconfig.LoadContextsAsync([kubeconfig]);
            using var narrow = await ClusterClient.ConnectAsync(contexts[0], ct);

            var sync = await Assert.That(async () => await narrow.SyncArgoApplicationAsync(argo.Applications, ArgoNamespace, name))
                .Throws<KubernetesApiException>();
            await Assert.That(sync!.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(sync.ServerMessage!).Contains("cannot patch resource \"applications\"");

            var terminate = await Assert.That(async () => await narrow.TerminateArgoOperationAsync(argo.Applications, ArgoNamespace, name))
                .Throws<KubernetesApiException>();
            await Assert.That(terminate!.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

            // Nothing was written: the Application carries no operation.
            await Assert.That((await ReadAsync(argo, name)).Raw.TryGetProperty("operation", out _)).IsFalse();
        }
        finally
        {
            await CleanUpAsync(argo, [(ArgoNamespace, name)]);
            foreach (var (descriptor, item) in new[]
            {
                (ResourceDescriptor.RoleBindings, reader), (ResourceDescriptor.Roles, reader),
            })
            {
                try
                {
                    await argo.Client.DeleteResourceAsync(descriptor, ArgoNamespace, item);
                }
                catch (KubernetesApiException)
                {
                    // already gone
                }
            }

            try
            {
                await argo.Client.DeleteResourceAsync(LiveCluster.ServiceAccounts, ArgoNamespace, reader);
            }
            catch (KubernetesApiException)
            {
                // already gone
            }

            if (kubeconfig is not null)
            {
                File.Delete(kubeconfig);
            }
        }
    }

    // ------------------------------------------------------------------ FEAT-74 (#193)

    /// <summary>
    /// Settles the subresource question against the real CRD: <c>…/status</c> is a 404, and the
    /// terminate patch of the object stops a sync stuck in a PreSync hook. Argo records the
    /// operation Failed, "Operation terminated", and never applies what came after the hook.
    /// A second terminate finds nothing running and writes nothing.
    /// </summary>
    [Test]
    public async Task Terminate_stops_a_running_sync_through_the_object_not_a_status_subresource(CancellationToken ct)
    {
        using var argo = await ConnectAsync(ct);
        var name = Named("slow");
        var destination = Named("slow-dest");
        try
        {
            await CreateApplicationAsync(argo, name, "slow", "main", destination);

            // No status subresource on Argo's CRD.
            using (var content = new StringContent("""{"status":{"operationState":{"phase":"Terminating"}}}""", Encoding.UTF8, "application/merge-patch+json"))
            using (var response = await argo.Client.SendRequestAsync(
                HttpMethod.Patch, argo.Applications.ItemPath(ArgoNamespace, name) + "/status", content, HttpCompletionOption.ResponseContentRead, ct))
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }

            await argo.Client.SyncArgoApplicationAsync(argo.Applications, ArgoNamespace, name);
            await WaitForPhaseAsync(argo, name, (p, m) => p == "Running" && m.Contains("hook", StringComparison.Ordinal),
                "the sync to wait on its PreSync hook", ct);
            await Assert.That(ArgoCd.ReadApplication(await ReadAsync(argo, name)).CanTerminate).IsTrue();

            await Assert.That(await argo.Client.TerminateArgoOperationAsync(argo.Applications, ArgoNamespace, name)).IsTrue();
            var phase = "";
            var message = "";
            await LiveCluster.WaitUntilAsync(async () =>
            {
                var raw = (await ReadAsync(argo, name)).Raw;
                phase = Str(raw, "status", "operationState", "phase");
                message = Str(raw, "status", "operationState", "message");
                return phase is "Failed" or "Error" or "Succeeded";
            }, Reconcile, "the terminated sync to end", ct);

            await Assert.That(phase).IsEqualTo("Failed");
            await Assert.That(message).IsEqualTo("Operation terminated");
            await Assert.That(await ConfigMapExistsAsync(argo, destination, "after-slow-hook")).IsFalse();

            await Assert.That(await argo.Client.TerminateArgoOperationAsync(argo.Applications, ArgoNamespace, name)).IsFalse();
            await Assert.That(Str((await ReadAsync(argo, name)).Raw, "status", "operationState", "phase")).IsEqualTo("Failed");
        }
        finally
        {
            await CleanUpAsync(argo, [(ArgoNamespace, name)], destination);
        }
    }

    // ------------------------------------------------------------------ VER-60 (#190)

    /// <summary>
    /// Applications in any namespace: Argo reconciles a tenant Application in
    /// <c>argo-tenant</c> and writes <c>status.controllerNamespace: argocd</c>, so "Open in Argo
    /// CD" reads <c>argocd-cm</c> from <c>argocd</c> and links to Argo's own URL. An
    /// Application in a namespace Argo does not watch, whose author wrote its own namespace
    /// into that field beside an <c>argocd-cm</c> of their own, gets no link.
    /// </summary>
    [Test]
    public async Task Open_in_argo_links_a_reconciled_tenant_application_and_refuses_a_self_claimed_one(CancellationToken ct)
    {
        using var argo = await ConnectAsync(ct);
        var tenant = Named("tenant");
        var rogueNamespace = Named("rogue");
        var rogue = Named("rogue-app");
        try
        {
            await CreateApplicationAsync(argo, tenant, "app", "with-removed", Named("tenant-dest"), project: "tenant", @namespace: TenantNamespace);
            await LiveCluster.WaitUntilAsync(
                async () => ArgoCd.ReadApplication(await ReadAsync(argo, tenant, TenantNamespace)).ControllerNamespace == ArgoNamespace,
                Reconcile, "Argo to reconcile the tenant Application", ct);

            await argo.Client.ApplyYamlAsync(Namespaces, null, rogueNamespace, $"""
                apiVersion: v1
                kind: Namespace
                metadata:
                  name: {rogueNamespace}
                """, FieldManager);
            await argo.Client.ApplyYamlAsync(ResourceDescriptor.ConfigMaps, rogueNamespace, ArgoUi.ConfigMapName, $"""
                apiVersion: v1
                kind: ConfigMap
                metadata:
                  name: {ArgoUi.ConfigMapName}
                  namespace: {rogueNamespace}
                data:
                  url: https://argocd.attacker.test
                """, FieldManager);
            await CreateApplicationAsync(argo, rogue, "app", "with-removed", rogueNamespace, @namespace: rogueNamespace,
                extraStatus: $"""
                status:
                  controllerNamespace: {rogueNamespace}
                """);

            var visible = (await argo.Client.ListArgoApplicationsAsync(argo.Applications, cancellationToken: ct)).ToList();
            var tenantApp = visible.Single(a => a.Name == tenant);
            var rogueApp = visible.Single(a => a.Name == rogue);

            // Argo left the rogue claim alone: its namespace is not one it watches.
            await Assert.That(rogueApp.ControllerNamespace).IsEqualTo(rogueNamespace);
            await Assert.That(ArgoUi.ConfigMapNamespace(rogueApp, visible)).IsNull();

            await Assert.That(ArgoUi.ConfigMapNamespace(tenantApp, visible)).IsEqualTo(ArgoNamespace);
            var cm = await argo.Client.ReadResourceAsync(ResourceDescriptor.ConfigMaps, ArgoNamespace, ArgoUi.ConfigMapName, ct);
            var url = ArgoUi.ApplicationUrl(ArgoUi.BaseUrl(cm)!, tenantApp);
            await Assert.That(url.AbsoluteUri).IsEqualTo($"https://argocd.sandbox.test/applications/{TenantNamespace}/{tenant}");
        }
        finally
        {
            await CleanUpAsync(argo, [(TenantNamespace, tenant), (rogueNamespace, rogue)], rogueNamespace, Named("tenant-dest"));
        }
    }

    /// <summary>A ServiceAccount in argocd that may get, list and watch Applications and nothing else; returns its kubeconfig.</summary>
    private static async Task<string> CreateReaderAsync(ClusterClient admin, string name, CancellationToken ct)
    {
        await admin.ApplyYamlAsync(LiveCluster.ServiceAccounts, ArgoNamespace, name, $"""
            apiVersion: v1
            kind: ServiceAccount
            metadata:
              name: {name}
              namespace: {ArgoNamespace}
            """, FieldManager, cancellationToken: ct);
        await admin.ApplyYamlAsync(ResourceDescriptor.Roles, ArgoNamespace, name, $"""
            apiVersion: rbac.authorization.k8s.io/v1
            kind: Role
            metadata:
              name: {name}
              namespace: {ArgoNamespace}
            rules:
              - apiGroups: [argoproj.io]
                resources: [applications]
                verbs: [get, list, watch]
            """, FieldManager, cancellationToken: ct);
        await admin.ApplyYamlAsync(ResourceDescriptor.RoleBindings, ArgoNamespace, name, $"""
            apiVersion: rbac.authorization.k8s.io/v1
            kind: RoleBinding
            metadata:
              name: {name}
              namespace: {ArgoNamespace}
            subjects:
              - kind: ServiceAccount
                name: {name}
                namespace: {ArgoNamespace}
            roleRef:
              apiGroup: rbac.authorization.k8s.io
              kind: Role
              name: {name}
            """, FieldManager, cancellationToken: ct);

        using var tokenRequest = new StringContent(
            """{"apiVersion":"authentication.k8s.io/v1","kind":"TokenRequest","spec":{"expirationSeconds":600}}""",
            Encoding.UTF8, "application/json");
        using var response = await admin.SendRequestAsync(
            HttpMethod.Post, $"api/v1/namespaces/{ArgoNamespace}/serviceaccounts/{name}/token",
            tokenRequest, HttpCompletionOption.ResponseContentRead, ct);
        await ClusterClient.EnsureSuccessAsync(response, ct);
        using var tokenDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var token = tokenDoc.RootElement.GetProperty("status").GetProperty("token").GetString()!;

        var sandbox = (await KubeconfigReader.LoadAsync(admin.Context.KubeconfigPath, ct)).Configuration;
        var contextEntry = sandbox.Contexts.First(c => c.Name == admin.Context.Name);
        var cluster = sandbox.Clusters.First(c => c.Name == contextEntry.ContextDetails.Cluster).ClusterEndpoint;
        var path = Path.Combine(Path.GetTempPath(), $"kubenimbus-argo-live-{name}-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(path, $"""
            apiVersion: v1
            kind: Config
            clusters:
              - name: argo
                cluster:
                  server: {cluster.Server}
                  certificate-authority-data: {cluster.CertificateAuthorityData}
            users:
              - name: {name}
                user:
                  token: {token}
            contexts:
              - name: {name}
                context:
                  cluster: argo
                  user: {name}
                  namespace: {ArgoNamespace}
            current-context: {name}
            """, ct);
        return path;
    }
}
