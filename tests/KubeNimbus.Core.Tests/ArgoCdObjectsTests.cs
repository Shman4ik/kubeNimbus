using System.Text.Json;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// The ApplicationSet and AppProject readers behind their detail panes (#194). The set's JSON
/// is the shape Argo CD v3.5.4 wrote for a two-element list generator in the Argo sandbox
/// (scripts/argo-sandbox-up.ps1); the project's covers each empty-list rule Argo applies.
/// </summary>
public class ArgoCdObjectsTests
{
    private static DynamicResource Parse(string json) => new(JsonDocument.Parse(json).RootElement.Clone());

    private const string SetJson = """
        {"apiVersion":"argoproj.io/v1alpha1","kind":"ApplicationSet",
         "metadata":{"name":"demo-set","namespace":"argocd","uid":"2f2bd477"},
         "spec":{"goTemplate":true,
           "generators":[{"list":{"elements":[{"env":"dev"},{"env":"qa"}]}}],
           "template":{"metadata":{"name":"demo-{{.env}}"},
             "spec":{"project":"default",
               "source":{"repoURL":"git://argo-git.argocd.svc.cluster.local:9418/demo.git","path":"app","targetRevision":"without-removed"},
               "destination":{"server":"https://kubernetes.default.svc","namespace":"demo-{{.env}}"}}}},
         "status":{
           "conditions":[
             {"type":"ErrorOccurred","status":"False","message":"All applications have been generated successfully","reason":"ApplicationSetUpToDate"},
             {"type":"ParametersGenerated","status":"True","message":"Successfully generated parameters for all Applications"},
             {"type":"ResourcesUpToDate","status":"True","message":"All applications have been generated successfully"}],
           "health":{"status":"Healthy"},
           "resources":[
             {"group":"argoproj.io","health":{},"kind":"Application","name":"demo-dev","namespace":"argocd","version":"v1alpha1"},
             {"group":"argoproj.io","health":{"status":"Degraded"},"status":"OutOfSync","kind":"Application","name":"demo-qa","namespace":"argocd","version":"v1alpha1"}]}}
        """;

    [Test]
    public async Task An_application_set_reads_its_generators_template_and_generated_applications()
    {
        var set = ArgoCd.ReadApplicationSet(Parse(SetJson));

        await Assert.That(set.Generators).IsEquivalentTo(["List · 2 elements"]);
        await Assert.That(set.TemplateName).IsEqualTo("demo-{{.env}}");
        await Assert.That(set.SourceSummary).IsEqualTo("without-removed · app");
        await Assert.That(set.Destination).IsEqualTo("demo-{{.env}} on https://kubernetes.default.svc");
        await Assert.That(set.Health).IsEqualTo("Healthy");
        await Assert.That(set.Generated.Select(g => g.Name)).IsEquivalentTo(["demo-dev", "demo-qa"]);
        await Assert.That(set.Generated[1].Health).IsEqualTo(ArgoHealthState.Degraded);
        await Assert.That(set.Generated[1].Sync).IsEqualTo(ArgoSyncState.OutOfSync);
    }

    /// <summary>
    /// A healthy set carries <c>ErrorOccurred: False</c>. Read by type alone, as Application
    /// conditions are, every healthy ApplicationSet would show a red condition.
    /// </summary>
    [Test]
    public async Task A_healthy_sets_conditions_are_not_problems()
    {
        var set = ArgoCd.ReadApplicationSet(Parse(SetJson));

        await Assert.That(set.Conditions.Any(c => c.IsProblem)).IsFalse();
    }

    [Test]
    public async Task An_error_that_occurred_and_a_failed_generation_are_problems()
    {
        var set = ArgoCd.ReadApplicationSet(Parse("""
            {"metadata":{"name":"s","namespace":"argocd"},
             "status":{"conditions":[
               {"type":"ErrorOccurred","status":"True","message":"repository not found"},
               {"type":"ParametersGenerated","status":"False","message":"error generating params"}]}}
            """));

        await Assert.That(set.Conditions.All(c => c.IsProblem)).IsTrue();
    }

    [Test]
    public async Task Generators_are_described_by_what_they_iterate()
    {
        static string Describe(string json) => ArgoCd.DescribeGenerator(JsonDocument.Parse(json).RootElement.Clone());

        await Assert.That(Describe("""{"clusters":{}}""")).IsEqualTo("Clusters · every cluster Argo CD knows");
        await Assert.That(Describe("""{"clusters":{"selector":{"matchLabels":{"env":"prod"}}}}""")).IsEqualTo("Clusters · labelled env=prod");
        await Assert.That(Describe("""{"git":{"repoURL":"https://g/x.git","revision":"main","directories":[{"path":"apps/*"},{"path":"apps/skip","exclude":true}]}}"""))
            .IsEqualTo("Git directories · apps/*, not apps/skip · https://g/x.git @ main");
        await Assert.That(Describe("""{"git":{"repoURL":"https://g/x.git","files":[{"path":"cfg/*.json"}]}}"""))
            .IsEqualTo("Git files · cfg/*.json · https://g/x.git @ HEAD");
        await Assert.That(Describe("""{"matrix":{"generators":[{"git":{}},{"clusters":{}}]}}""")).IsEqualTo("Matrix of git and clusters");
        await Assert.That(Describe("""{"pullRequest":{"github":{"owner":"o"}}}""")).IsEqualTo("Pull requests · github");
        await Assert.That(Describe("""{"somethingNew":{}}""")).IsEqualTo("somethingNew");
    }

    [Test]
    public async Task The_set_policy_says_what_happens_to_generated_applications()
    {
        var set = ArgoCd.ReadApplicationSet(Parse("""
            {"metadata":{"name":"s","namespace":"argocd"},
             "spec":{"syncPolicy":{"applicationsSync":"create-only","preserveResourcesOnDeletion":true}}}
            """));

        await Assert.That(set.PolicySummary).IsEqualTo(
            "Creates Applications; never updates or deletes them. Deleting an Application leaves its resources on the cluster.");
    }

    [Test]
    public async Task An_empty_project_allows_no_repository_destination_or_cluster_kind_but_every_namespaced_kind()
    {
        var project = ArgoCd.ReadProject(Parse("""{"metadata":{"name":"locked","namespace":"argocd"},"spec":{}}"""));

        await Assert.That(project.SourceReposRule).IsEqualTo("No repository is allowed: the list is empty.");
        await Assert.That(project.DestinationsRule).IsEqualTo("No destination is allowed: the list is empty.");
        await Assert.That(project.ClusterKindsRule).IsEqualTo("No cluster-scoped kind may be deployed: the allow list is empty.");
        await Assert.That(project.NamespacedKindsRule).IsEqualTo("Every namespaced kind.");
        await Assert.That(project.SourceNamespacesRule).IsEqualTo("Only Applications in Argo CD's own namespace.");
    }

    /// <summary>Argo reads an absent namespaced allow list as "all" and an empty one as "none"; so must the pane.</summary>
    [Test]
    public async Task A_set_but_empty_namespaced_allow_list_allows_nothing()
    {
        var project = ArgoCd.ReadProject(Parse("""
            {"metadata":{"name":"p","namespace":"argocd"},"spec":{"namespaceResourceWhitelist":[]}}
            """));

        await Assert.That(project.NamespacedKindsRule).IsEqualTo("No namespaced kind may be deployed: the allow list is set and empty.");
    }

    [Test]
    public async Task A_project_reads_its_lists_windows_and_roles()
    {
        var project = ArgoCd.ReadProject(Parse("""
            {"metadata":{"name":"tenant","namespace":"argocd"},
             "spec":{"description":"Team payments",
               "sourceRepos":["https://git.example/payments/*","!https://git.example/payments/secret"],
               "sourceNamespaces":["argo-tenant"],
               "destinations":[{"server":"https://kubernetes.default.svc","namespace":"payments-*"},{"name":"prod-eu"}],
               "clusterResourceWhitelist":[{"group":"","kind":"Namespace"}],
               "namespaceResourceBlacklist":[{"group":"","kind":"ResourceQuota"}],
               "syncWindows":[{"kind":"deny","schedule":"0 22 * * *","duration":"8h","applications":["*"],"manualSync":false,"timeZone":"UTC"}],
               "roles":[{"name":"deployer","description":"CI","policies":["p, proj:tenant:deployer, applications, sync, tenant/*, allow"],"groups":["ci"]}]}}
            """));

        await Assert.That(project.Description).IsEqualTo("Team payments");
        await Assert.That(project.SourceReposRule).IsEqualTo("");
        await Assert.That(project.Destinations.Select(d => d.Text)).IsEquivalentTo(["payments-* on https://kubernetes.default.svc", "* on prod-eu"]);
        await Assert.That(project.ClusterResourceAllow).IsEquivalentTo(["core/Namespace"]);
        await Assert.That(project.NamespacedKindsRule).IsEqualTo("Every namespaced kind except the deny list below.");
        await Assert.That(project.SyncWindows[0].When).IsEqualTo("deny · 0 22 * * * for 8h (UTC)");
        await Assert.That(project.SyncWindows[0].Scope).IsEqualTo("Matches applications *. Manual syncs are blocked too.");
        await Assert.That(project.Roles[0].Summary).IsEqualTo("CI · 1 policy · groups ci");
    }

    [Test]
    public async Task A_fleet_read_sums_the_counts_across_clusters()
    {
        static ArgoFleetApplication App(string cluster, string sync, string health) => new(
            cluster,
            new ResourceDescriptor("argoproj.io", "v1alpha1", "Application", "applications", "application", true, [], []),
            ArgoCd.ReadApplication(Parse("""
                {"metadata":{"name":"checkout","namespace":"argocd"},
                 "status":{"sync":{"status":"SYNC"},"health":{"status":"HEALTH"}}}
                """.Replace("SYNC", sync, StringComparison.Ordinal).Replace("HEALTH", health, StringComparison.Ordinal))));

        var read = new ArgoFleetRead(
            [App("eu", "Synced", "Healthy"), App("us", "OutOfSync", "Degraded"), App("ap", "Synced", "Healthy")],
            Serving: 3, Members: 4, Failed: []);

        await Assert.That(read.Counts).IsEqualTo(new ArgoSummary(3, 2, 2, 1, 1, 0, 0));
        await Assert.That(read.Summary).IsEqualTo("3 of 4 clusters run Argo CD");
    }
}
