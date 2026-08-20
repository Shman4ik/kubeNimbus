using System.Text.Json;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Pure unit tests (no cluster needed) for the Argo CD integration: the two patch bodies,
/// the state parsing, and the classification the dashboard is ordered by.
///
/// <para>
/// The patch bodies carry the same weight <c>WorkloadActionsTests</c> carries, for the same
/// reason: both of Argo's actions fail <em>silently</em> when the body is wrong. A refresh
/// written under a misspelled annotation key and a sync written into <c>spec</c> instead of
/// the object's top-level <c>operation</c> are both a 200 from the API server that no
/// controller ever acts on, and from the UI that is indistinguishable from a dead button.
/// </para>
/// </summary>
public class ArgoCdTests
{
    private static DynamicResource Parse(string json) =>
        new(JsonDocument.Parse(json).RootElement.Clone());

    private static ResourceDescriptor Descriptor(
        string group = "argoproj.io", string kind = "Application", string[]? verbs = null) =>
        new(group, "v1alpha1", kind, kind.ToLowerInvariant() + "s", kind.ToLowerInvariant(), true, [], [])
        {
            Verbs = verbs ?? [],
        };

    private const string HealthyJson = """
        {
          "apiVersion": "argoproj.io/v1alpha1",
          "kind": "Application",
          "metadata": { "name": "checkout", "namespace": "argocd" },
          "spec": {
            "project": "payments",
            "source": {
              "repoURL": "https://example.invalid/manifests.git",
              "path": "apps/checkout",
              "targetRevision": "main"
            },
            "destination": { "server": "https://kubernetes.default.svc", "namespace": "payments" },
            "syncPolicy": { "automated": { "prune": true, "selfHeal": true } }
          },
          "status": {
            "sync": { "status": "Synced", "revision": "8f3c1d94b27ae5106f4e2c0b9a7d3e51c8b6042f" },
            "health": { "status": "Healthy" },
            "operationState": { "phase": "Succeeded", "message": "successfully synced" },
            "resources": [
              { "group": "apps", "version": "v1", "kind": "Deployment", "namespace": "payments", "name": "checkout",
                "status": "Synced", "health": { "status": "Healthy" } },
              { "version": "v1", "kind": "ConfigMap", "namespace": "payments", "name": "checkout", "status": "Synced" }
            ],
            "history": [
              { "id": 1, "revision": "aaa1111", "deployedAt": "2026-07-01T10:00:00Z" },
              { "id": 2, "revision": "bbb2222", "deployedAt": "2026-07-20T10:00:00Z" }
            ]
          }
        }
        """;

    // ------------------------------------------------------------ patch bodies

    /// <summary>
    /// The exact bytes of a refresh. The key is the one Argo's application controller
    /// watches for; anything else is an annotation nobody reads.
    /// </summary>
    [Test]
    public async Task Refresh_patch_sets_argos_own_annotation()
    {
        await Assert.That(ArgoCd.RefreshPatch(hard: false)).IsEqualTo(
            """{"metadata":{"annotations":{"argocd.argoproj.io/refresh":"normal"}}}""");

        await Assert.That(ArgoCd.RefreshPatch(hard: true)).IsEqualTo(
            """{"metadata":{"annotations":{"argocd.argoproj.io/refresh":"hard"}}}""");
    }

    /// <summary>
    /// A refresh touches the annotations and nothing else. As an RFC 7386 merge patch every
    /// object along the path is merged rather than replaced, so the object's other
    /// annotations — including <c>kubectl.kubernetes.io/last-applied-configuration</c> and
    /// whatever Argo's own notifications controller keeps there — survive.
    /// </summary>
    [Test]
    public async Task Refresh_patch_touches_only_the_annotations()
    {
        using var doc = JsonDocument.Parse(ArgoCd.RefreshPatch(hard: false));
        var root = doc.RootElement;

        await Assert.That(root.EnumerateObject().Count()).IsEqualTo(1);
        var metadata = root.GetProperty("metadata");
        await Assert.That(metadata.EnumerateObject().Count()).IsEqualTo(1);
        await Assert.That(metadata.GetProperty("annotations").EnumerateObject().Count()).IsEqualTo(1);
    }

    /// <summary>
    /// The exact bytes of a sync. It goes into the Application's <b>top-level</b>
    /// <c>operation</c> — not into <c>spec</c>, and not into <c>status</c> — which is the
    /// field the application controller watches and the one Argo's own API server writes.
    /// </summary>
    [Test]
    public async Task Sync_patch_writes_the_top_level_operation()
    {
        await Assert.That(ArgoCd.SyncPatch(prune: false)).IsEqualTo(
            """{"operation":{"initiatedBy":{"username":"kubenimbus"},"info":[{"name":"Reason","value":"Sync requested from kubeNimbus"}],"sync":{"prune":false,"syncStrategy":{"hook":{}}}}}""");
    }

    [Test]
    public async Task Sync_patch_carries_the_prune_decision()
    {
        using var doc = JsonDocument.Parse(ArgoCd.SyncPatch(prune: true));

        await Assert.That(doc.RootElement.GetProperty("operation").GetProperty("sync")
            .GetProperty("prune").GetBoolean()).IsTrue();
    }

    /// <summary>
    /// No revision is written, and that is the decision the whole action rests on: omitting
    /// it makes Argo sync to the Application's own <c>targetRevision</c>, which is what the
    /// Application says it wants. Pinning one here would turn "Sync" into "deploy something
    /// else", silently.
    /// </summary>
    [Test]
    public async Task Sync_patch_pins_no_revision()
    {
        using var doc = JsonDocument.Parse(ArgoCd.SyncPatch(prune: false));
        var sync = doc.RootElement.GetProperty("operation").GetProperty("sync");

        await Assert.That(sync.TryGetProperty("revision", out _)).IsFalse();
    }

    // ------------------------------------------------------- capability rules

    [Test]
    public async Task Sync_is_offered_for_the_application_kind_a_server_says_is_patchable()
    {
        await Assert.That(ArgoCd.SupportsSync(Descriptor(verbs: ["get", "list", "patch"]))).IsTrue();

        // Unknown verbs mean "the server did not say", never "no" — the same rule
        // ResourceDescriptor.AllowsVerb states, and what keeps hand-built descriptors
        // (the demo catalog, fixtures) from silently losing the action.
        await Assert.That(ArgoCd.SupportsSync(Descriptor())).IsTrue();
    }

    [Test]
    public async Task Sync_is_refused_where_the_server_says_the_kind_is_not_patchable()
    {
        await Assert.That(ArgoCd.SupportsSync(Descriptor(verbs: ["get", "list"]))).IsFalse();
    }

    /// <summary>
    /// Argo Rollouts, Argo Workflows and a same-named CRD from another vendor are all not
    /// this. The kind check is narrow on purpose: <c>operation</c> is a field of the Argo CD
    /// Application schema, and patching it into anything else is a 422 at best.
    /// </summary>
    [Test]
    [Arguments("argoproj.io", "Rollout")]
    [Arguments("argoproj.io", "Workflow")]
    [Arguments("argoproj.io", "ApplicationSet")]
    [Arguments("example.com", "Application")]
    public async Task Sync_is_refused_for_every_other_kind(string group, string kind)
    {
        await Assert.That(ArgoCd.SupportsSync(Descriptor(group, kind))).IsFalse();
    }

    /// <summary>The sidebar buckets on the group, which is Argo's whole product family.</summary>
    [Test]
    public async Task Every_argoproj_kind_is_an_argo_kind()
    {
        await Assert.That(ArgoCd.IsArgoKind(Descriptor("argoproj.io", "Rollout"))).IsTrue();
        await Assert.That(ArgoCd.IsArgoKind(Descriptor("cert-manager.io", "Certificate"))).IsFalse();
    }

    [Test]
    public async Task The_application_descriptor_is_found_by_group_and_kind_at_whatever_version_the_server_serves()
    {
        ResourceDescriptor[] catalog =
        [
            Descriptor("argoproj.io", "AppProject"),
            new("argoproj.io", "v1beta1", "Application", "applications", "application", true, [], []),
        ];

        var found = ArgoCd.ApplicationDescriptor(catalog);

        await Assert.That(found?.Version).IsEqualTo("v1beta1");
        await Assert.That(ArgoCd.ApplicationDescriptor([Descriptor("argoproj.io", "Rollout")])).IsNull();
    }

    // ----------------------------------------------------------------- parsing

    [Test]
    public async Task An_application_is_read_into_the_fields_the_dashboard_shows()
    {
        var app = ArgoCd.ReadApplication(Parse(HealthyJson));

        await Assert.That(app.Key).IsEqualTo("argocd/checkout");
        await Assert.That(app.Project).IsEqualTo("payments");
        await Assert.That(app.Sync).IsEqualTo(ArgoSyncState.Synced);
        await Assert.That(app.Health).IsEqualTo(ArgoHealthState.Healthy);
        await Assert.That(app.ShortRevision).IsEqualTo("8f3c1d9");
        await Assert.That(app.RepoUrl).IsEqualTo("https://example.invalid/manifests.git");
        await Assert.That(app.SourcePath).IsEqualTo("apps/checkout");
        await Assert.That(app.TargetRevision).IsEqualTo("main");
        await Assert.That(app.DestinationNamespace).IsEqualTo("payments");
        await Assert.That(app.Resources.Count).IsEqualTo(2);
        await Assert.That(app.SyncPolicySummary).IsEqualTo("Automated (prune, self-heal)");
    }

    /// <summary>
    /// Newest first — Argo appends to <c>status.history</c>, and "what changed last" is the
    /// question the list is opened with.
    /// </summary>
    [Test]
    public async Task History_is_newest_first()
    {
        var app = ArgoCd.ReadApplication(Parse(HealthyJson));

        await Assert.That(app.History[0].Id).IsEqualTo(2L);
        await Assert.That(app.History[1].Id).IsEqualTo(1L);
    }

    /// <summary>
    /// A multi-source Application reads its first source and says how many more there are,
    /// rather than showing one of three as though it were the whole story.
    /// </summary>
    [Test]
    public async Task A_multi_source_application_reads_its_first_source_and_counts_the_rest()
    {
        var app = ArgoCd.ReadApplication(Parse("""
            {
              "kind": "Application",
              "metadata": { "name": "monitoring", "namespace": "argocd" },
              "spec": {
                "sources": [
                  { "repoURL": "https://charts.invalid", "chart": "kube-prometheus-stack", "targetRevision": "62.3.0" },
                  { "repoURL": "https://example.invalid/manifests.git", "path": "values", "targetRevision": "main" }
                ]
              }
            }
            """));

        await Assert.That(app.SourceCount).IsEqualTo(2);
        await Assert.That(app.SourcePath).IsEqualTo("kube-prometheus-stack");
        await Assert.That(app.SourceSummary).IsEqualTo("62.3.0 · kube-prometheus-stack (+1 more)");
    }

    /// <summary>
    /// An Application Argo has never managed to compare carries no status at all. Every
    /// field has to come back empty rather than throwing — this is the object the pane is
    /// opened on precisely because something is wrong with it.
    /// </summary>
    [Test]
    public async Task An_application_with_no_status_reads_as_unknown_rather_than_throwing()
    {
        var app = ArgoCd.ReadApplication(Parse("""
            { "kind": "Application", "metadata": { "name": "new", "namespace": "argocd" }, "spec": {} }
            """));

        await Assert.That(app.Sync).IsEqualTo(ArgoSyncState.Unknown);
        await Assert.That(app.Health).IsEqualTo(ArgoHealthState.Unknown);
        await Assert.That(app.Project).IsEqualTo("default");
        await Assert.That(app.Resources).IsEmpty();
        await Assert.That(app.History).IsEmpty();
        await Assert.That(app.SyncPolicySummary).IsEqualTo("Manual");
    }

    /// <summary>
    /// A state Argo adds in a future version is <c>Unknown</c>, never a guess. Same rule
    /// pod detail's Overview tab settled for an unclassified condition: being wrong
    /// confidently is worse than admitting the state is not recognized.
    /// </summary>
    [Test]
    [Arguments("Syncing")]
    [Arguments("synced")]
    [Arguments("")]
    public async Task An_unrecognized_sync_state_is_unknown(string value)
    {
        await Assert.That(ArgoCd.ParseSync(value)).IsEqualTo(ArgoSyncState.Unknown);
    }

    // ------------------------------------------------------- classification

    /// <summary>
    /// Health outranks sync. A Degraded Application whose sync is also wrong is filed as
    /// degraded: the pods are down either way, and being told it is out of sync sends
    /// somebody to Git when the problem is in the cluster.
    /// </summary>
    [Test]
    public async Task Health_outranks_sync_when_both_are_wrong()
    {
        var app = ArgoCd.ReadApplication(Parse("""
            {
              "kind": "Application",
              "metadata": { "name": "broken", "namespace": "argocd" },
              "spec": {},
              "status": { "sync": { "status": "OutOfSync" }, "health": { "status": "Degraded" } }
            }
            """));

        await Assert.That(app.NeedsAttention).IsTrue();
        await Assert.That(app.AttentionReason).IsEqualTo("Degraded");
    }

    /// <summary>
    /// Synced and Degraded is the case this whole two-pill design exists for: Git applied
    /// cleanly and the workload is failing. It must still need attention.
    /// </summary>
    [Test]
    public async Task Synced_but_degraded_still_needs_attention()
    {
        var app = ArgoCd.ReadApplication(Parse("""
            {
              "kind": "Application",
              "metadata": { "name": "fraud", "namespace": "argocd" },
              "spec": {},
              "status": { "sync": { "status": "Synced" }, "health": { "status": "Degraded" } }
            }
            """));

        await Assert.That(app.NeedsAttention).IsTrue();
        await Assert.That(app.AttentionReason).IsEqualTo("Degraded");
    }

    /// <summary>
    /// Progressing is the system working, not a problem. A dashboard that flagged every
    /// rollout in flight would be flagging nothing.
    /// </summary>
    [Test]
    public async Task A_progressing_application_that_is_synced_does_not_need_attention()
    {
        var app = ArgoCd.ReadApplication(Parse("""
            {
              "kind": "Application",
              "metadata": { "name": "rolling", "namespace": "argocd" },
              "spec": {},
              "status": { "sync": { "status": "Synced" }, "health": { "status": "Progressing" } }
            }
            """));

        await Assert.That(app.NeedsAttention).IsFalse();
        await Assert.That(app.AttentionReason).IsEqualTo("");
    }

    /// <summary>
    /// The seven counts overlap by construction — Synced and Healthy describe the same
    /// Applications from two directions — so none of them may be derived from another.
    /// </summary>
    [Test]
    public async Task The_summary_counts_sync_and_health_independently()
    {
        ArgoApplication[] applications =
        [
            ArgoCd.ReadApplication(Parse(HealthyJson)),
            ArgoCd.ReadApplication(Parse("""
                {"kind":"Application","metadata":{"name":"a","namespace":"argocd"},"spec":{},
                 "status":{"sync":{"status":"Synced"},"health":{"status":"Degraded"}}}
                """)),
            ArgoCd.ReadApplication(Parse("""
                {"kind":"Application","metadata":{"name":"b","namespace":"argocd"},"spec":{},
                 "status":{"sync":{"status":"OutOfSync"},"health":{"status":"Missing"}}}
                """)),
            ArgoCd.ReadApplication(Parse("""
                {"kind":"Application","metadata":{"name":"c","namespace":"argocd"},"spec":{},
                 "status":{"sync":{"status":"OutOfSync"},"health":{"status":"Progressing"}}}
                """)),
        ];

        var summary = ArgoSummary.Of(applications);

        await Assert.That(summary.Total).IsEqualTo(4);
        await Assert.That(summary.Synced).IsEqualTo(2);
        await Assert.That(summary.Healthy).IsEqualTo(1);
        await Assert.That(summary.OutOfSync).IsEqualTo(2);
        await Assert.That(summary.Degraded).IsEqualTo(1);
        await Assert.That(summary.Missing).IsEqualTo(1);
        await Assert.That(summary.Progressing).IsEqualTo(1);
        await Assert.That(summary.IsAllWell).IsFalse();
    }

    /// <summary>
    /// Argo raises a condition to report a fault, which is the opposite of how Kubernetes
    /// uses them — so an unrecognized <c>*Error</c> type from a newer Argo reads as a
    /// problem rather than as fine by default.
    /// </summary>
    [Test]
    [Arguments("ComparisonError", true)]
    [Arguments("SyncError", true)]
    [Arguments("InvalidSpecError", true)]
    [Arguments("SomeFutureError", true)]
    [Arguments("SharedResourceWarning", false)]
    public async Task A_condition_type_ending_in_error_is_a_problem(string type, bool expected)
    {
        await Assert.That(new ArgoCondition(type, "", null).IsProblem).IsEqualTo(expected);
    }
}
