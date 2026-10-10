using System.Net;
using System.Text.Json;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// VER-61 (#191), the cluster half: the Core calls behind the actions UI rule 17 lets fire
/// on their click — a CronJob's suspend and its resume, Argo's refresh and sync, and the
/// "Sync with prune…" confirm — against a real API server. Each asserts what the server
/// stored and what a watch of the kind reported, not that the patch was accepted. Cordon
/// and uncordon are <c>NodeOperationsLiveTests</c>' and are not repeated here.
/// </summary>
/// <remarks>
/// The sandbox carries Argo CD's CRDs but no controller, so an Application here is a
/// stand-in: nothing acts on the refresh annotation or the operation, which is exactly
/// what lets the test read back what the app wrote. On a cluster with a controller the
/// annotation is removed once Argo has re-compared.
/// </remarks>
public class OneClickActionsLiveTests
{
    private static readonly ResourceDescriptor CronJobs = new(
        "batch", "v1", "CronJob", "cronjobs", "cronjob", true, ["cj"], ["all"]);

    private static readonly ResourceDescriptor Applications = new(
        "argoproj.io", "v1alpha1", "Application", "applications", "application", true, ["app", "apps"], []);

    /// <summary>
    /// Suspend, then resume: the field lands, the watched list sees each change as a
    /// Modified event, and the server's own Table (what <c>kubectl get cronjobs</c> prints)
    /// says <c>True</c> and then <c>False</c> in its SUSPEND column.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task Suspend_and_resume_land_on_the_cronjob_and_the_watch_follows(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("cron");
        await LiveCluster.ApplyAsync(client, CronJobs, name, CronJobYaml(name), ct);

        var seen = new List<bool>();
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var synced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in client.WatchResourceAsync(CronJobs, LiveCluster.Namespace, cancellationToken: watchCts.Token))
                {
                    if (evt.Type == ResourceEventType.Synced)
                    {
                        synced.TrySetResult();
                    }
                    else if (evt is { Type: ResourceEventType.Modified, Resource: { } cj } && cj.Name == name)
                    {
                        lock (seen)
                        {
                            if (seen.Count == 0 || seen[^1] != Suspended(cj))
                            {
                                seen.Add(Suspended(cj));
                            }

                            if (seen.SequenceEqual([true, false]))
                            {
                                bothSeen.TrySetResult();
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // ended by the test
            }
        }, watchCts.Token);
        await synced.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        await client.SetCronJobSuspendedAsync(CronJobs, LiveCluster.Namespace, name, suspended: true, ct);
        await Assert.That(Suspended((await client.ReadResourceAsync(CronJobs, LiveCluster.Namespace, name, ct))!)).IsTrue();
        await Assert.That(await SuspendColumnAsync(client, name, ct)).IsEqualTo("True");

        await client.SetCronJobSuspendedAsync(CronJobs, LiveCluster.Namespace, name, suspended: false, ct);
        await Assert.That(Suspended((await client.ReadResourceAsync(CronJobs, LiveCluster.Namespace, name, ct))!)).IsFalse();
        await Assert.That(await SuspendColumnAsync(client, name, ct)).IsEqualTo("False");

        await bothSeen.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        await watchCts.CancelAsync();
        await watch;
    }

    /// <summary>
    /// Refresh writes Argo's own annotation, <c>normal</c> or <c>hard</c>, and changes
    /// nothing else on the Application — its spec is exactly what it was.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task Refresh_writes_argos_annotation_and_nothing_else(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("argo-refresh");
        var before = await LiveCluster.ApplyAsync(client, Applications, name, ApplicationYaml(name), ct);

        await client.RefreshArgoApplicationAsync(Applications, LiveCluster.Namespace, name, cancellationToken: ct);
        var normal = await client.ReadResourceAsync(Applications, LiveCluster.Namespace, name, ct);
        await Assert.That(normal!.Annotations[ArgoCd.RefreshAnnotation]).IsEqualTo("normal");
        await Assert.That(normal.Raw.GetProperty("spec").GetRawText()).IsEqualTo(before.Raw.GetProperty("spec").GetRawText());
        await Assert.That(normal.Raw.TryGetProperty("operation", out _)).IsFalse();

        await client.RefreshArgoApplicationAsync(Applications, LiveCluster.Namespace, name, hard: true, cancellationToken: ct);
        var hard = await client.ReadResourceAsync(Applications, LiveCluster.Namespace, name, ct);
        await Assert.That(hard!.Annotations[ArgoCd.RefreshAnnotation]).IsEqualTo("hard");
    }

    /// <summary>
    /// A sync writes the top-level <c>operation</c> Argo's controller watches for: prune off
    /// for the one-click Sync, on for "Sync with prune…", and the initiator is the user the
    /// API server says this connection is.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task Sync_writes_the_operation_with_prune_only_when_asked(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var name = LiveCluster.Named("argo-sync");
        await LiveCluster.ApplyAsync(client, Applications, name, ApplicationYaml(name), ct);
        var me = await client.GetCurrentUsernameAsync(ct);

        await client.SyncArgoApplicationAsync(Applications, LiveCluster.Namespace, name, prune: false, ct);
        var plain = (await client.ReadResourceAsync(Applications, LiveCluster.Namespace, name, ct))!.Raw.GetProperty("operation");
        await Assert.That(plain.GetProperty("sync").GetProperty("prune").GetBoolean()).IsFalse();
        await Assert.That(plain.GetProperty("initiatedBy").GetProperty("username").GetString())
            .IsEqualTo(ArgoCd.InitiatorFor(me));

        await client.SyncArgoApplicationAsync(Applications, LiveCluster.Namespace, name, prune: true, ct);
        var pruned = (await client.ReadResourceAsync(Applications, LiveCluster.Namespace, name, ct))!.Raw.GetProperty("operation");
        await Assert.That(pruned.GetProperty("sync").GetProperty("prune").GetBoolean()).IsTrue();
    }

    /// <summary>
    /// A user allowed to read but not to patch gets the API server's own sentence for the
    /// suspend and for both Argo patches — naming the user, the verb, the resource and the
    /// namespace, which is what the strip prints beside Close — and nothing changes.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task A_narrow_user_gets_the_servers_own_403_for_suspend_sync_and_refresh(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var cron = LiveCluster.Named("cron-rbac");
        var app = LiveCluster.Named("argo-rbac");
        await LiveCluster.ApplyAsync(client, CronJobs, cron, CronJobYaml(cron), ct);
        await LiveCluster.ApplyAsync(client, Applications, app, ApplicationYaml(app), ct);

        using var narrow = await LiveCluster.CreateNarrowUserAsync(client, LiveCluster.Named("oneclick-viewer"), """
            - apiGroups: [batch]
              resources: [cronjobs]
              verbs: [get, list, watch]
            - apiGroups: [argoproj.io]
              resources: [applications]
              verbs: [get, list, watch]
            """, ct);
        await LiveCluster.WaitUntilAsync(async () =>
        {
            try
            {
                return await narrow.Client.ReadResourceAsync(Applications, LiveCluster.Namespace, app, ct) is not null;
            }
            catch (KubernetesApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                return false;
            }
        }, TimeSpan.FromSeconds(30), "the narrow user's read grant", ct);

        var suspend = await CaptureAsync(() => narrow.Client.SetCronJobSuspendedAsync(CronJobs, LiveCluster.Namespace, cron, true, ct));
        var sync = await CaptureAsync(() => narrow.Client.SyncArgoApplicationAsync(Applications, LiveCluster.Namespace, app, false, ct));
        var refresh = await CaptureAsync(() => narrow.Client.RefreshArgoApplicationAsync(Applications, LiveCluster.Namespace, app, cancellationToken: ct));

        foreach (var (failure, resource) in new[] { (suspend, "cronjobs"), (sync, "applications"), (refresh, "applications") })
        {
            await Assert.That(failure).IsNotNull();
            await Assert.That(failure!.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(failure.ServerMessage!).Contains(narrow.UserName);
            await Assert.That(failure.ServerMessage!).Contains($"cannot patch resource \"{resource}\"");
            await Assert.That(failure.ServerMessage!).Contains($"in the namespace \"{LiveCluster.Namespace}\"");
            await Assert.That(failure.Message).StartsWith(failure.ServerMessage!);
        }

        await Assert.That(Suspended((await client.ReadResourceAsync(CronJobs, LiveCluster.Namespace, cron, ct))!)).IsFalse();
        var untouched = await client.ReadResourceAsync(Applications, LiveCluster.Namespace, app, ct);
        await Assert.That(untouched!.Raw.TryGetProperty("operation", out _)).IsFalse();
        await Assert.That(untouched.Annotations.ContainsKey(ArgoCd.RefreshAnnotation)).IsFalse();
    }

    private static string CronJobYaml(string name) => $$"""
        apiVersion: batch/v1
        kind: CronJob
        metadata:
          name: {{name}}
          namespace: {{LiveCluster.Namespace}}
        spec:
          schedule: "0 0 1 1 *"
          suspend: false
          jobTemplate:
            spec:
              template:
                spec:
                  restartPolicy: Never
                  containers:
                    - name: job
                      image: {{LiveCluster.Image}}
                      imagePullPolicy: IfNotPresent
                      command: ["true"]
        """;

    private static string ApplicationYaml(string name) => $$"""
        apiVersion: argoproj.io/v1alpha1
        kind: Application
        metadata:
          name: {{name}}
          namespace: {{LiveCluster.Namespace}}
        spec:
          project: default
          destination:
            namespace: {{LiveCluster.Namespace}}
            server: https://kubernetes.default.svc
          source:
            repoURL: https://example.invalid/live-tests.git
            path: apps/none
            targetRevision: main
        """;

    private static bool Suspended(DynamicResource cronJob) =>
        cronJob.Raw.GetProperty("spec").TryGetProperty("suspend", out var s) && s.ValueKind == JsonValueKind.True;

    private static async Task<string> SuspendColumnAsync(ClusterClient client, string name, CancellationToken ct)
    {
        using var table = await LiveCluster.GetTableAsync(client, CronJobs.CollectionPath(LiveCluster.Namespace), ct);
        return ServerTable.Text(table.Row($"{LiveCluster.Namespace}/{name}")[table.IndexOf("Suspend")]);
    }

    private static async Task<KubernetesApiException?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (KubernetesApiException ex)
        {
            return ex;
        }
    }
}
