using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// Pure unit tests (no cluster needed) for the context-name environment guess
/// that colours the shell. Every case here is a safety property, not a taste
/// call: the whole point of the colour is that a production cluster looks
/// different from a sandbox before you click anything, so a name that reads as
/// production must classify as production, and — just as important — the
/// explicit non-production names must not, or the signal becomes noise people
/// learn to ignore.
/// </summary>
public class ClusterEnvironmentTests
{
    [Test]
    [Arguments("prod")]
    [Arguments("production")]
    [Arguments("my-prod-cluster")]
    [Arguments("eks-prod-01")]
    [Arguments("prod1")]
    [Arguments("acme_prd_euw1")]
    [Arguments("live")]
    [Arguments("PROD")]
    [Arguments("gke_acme-corp_europe-west4_prod")]
    [Arguments("arn:aws:eks:eu-west-1:123456789012:cluster/payments-prod")]
    public async Task Reads_production_names_as_production(string name) =>
        await Assert.That(ClusterEnvironments.Classify(name)).IsEqualTo(ClusterEnvironment.Production);

    /// <summary>
    /// The rescue list. Every one of these contains a production marker as a
    /// substring and means the opposite — a plain Contains would paint the whole
    /// pre-production estate red.
    /// </summary>
    [Test]
    [Arguments("preprod")]
    [Arguments("pre-prod")]
    [Arguments("preprod-eu")]
    [Arguments("nonprod")]
    [Arguments("non-prod-sandbox")]
    [Arguments("acme-notprod")]
    public async Task Rescues_explicit_non_production_names(string name) =>
        await Assert.That(ClusterEnvironments.Classify(name)).IsEqualTo(ClusterEnvironment.Staging);

    [Test]
    [Arguments("staging")]
    [Arguments("stage")]
    [Arguments("acme-stg-01")]
    [Arguments("uat")]
    [Arguments("qa-cluster")]
    [Arguments("canary")]
    public async Task Reads_staging_names_as_staging(string name) =>
        await Assert.That(ClusterEnvironments.Classify(name)).IsEqualTo(ClusterEnvironment.Staging);

    [Test]
    [Arguments("dev")]
    [Arguments("development")]
    [Arguments("acme-dev-2")]
    [Arguments("minikube")]
    [Arguments("docker-desktop")]
    [Arguments("kind-kubenimbus")]
    [Arguments("k3s-sandbox")]
    [Arguments("orbstack")]
    [Arguments("demo")]
    public async Task Reads_development_names_as_development(string name) =>
        await Assert.That(ClusterEnvironments.Classify(name)).IsEqualTo(ClusterEnvironment.Development);

    /// <summary>
    /// The built-in demo cluster carries sample data and no cluster at all, so the one
    /// thing it must never be coloured as is production — that would put a red band
    /// under a screen full of objects that do not exist.
    /// </summary>
    [Test]
    public async Task Demo_cluster_is_not_production() =>
        await Assert.That(ClusterEnvironments.Classify(ClusterContext.Demo.Name, ClusterContext.Demo.ClusterName))
            .IsEqualTo(ClusterEnvironment.Development);

    [Test]
    public async Task Demo_context_is_flagged_as_demo()
    {
        await Assert.That(ClusterContext.Demo.IsDemo).IsTrue();
        await Assert.That(new ClusterContext("prod", "prod", null, null, "/home/u/.kube/config").IsDemo).IsFalse();
    }

    /// <summary>
    /// A marker hiding inside an unrelated word must not fire. These are the
    /// cases that make a substring implementation useless in practice — the
    /// colour has to mean something, and "internal-tools" is not an integration
    /// environment.
    /// </summary>
    [Test]
    [Arguments("product-catalog")]
    [Arguments("internal-tools")]
    [Arguments("liveness-probe-testbed")]
    [Arguments("cluster-42")]
    [Arguments("")]
    public async Task Does_not_match_markers_inside_unrelated_words(string name) =>
        await Assert.That(ClusterEnvironments.Classify(name)).IsEqualTo(ClusterEnvironment.Unknown);

    /// <summary>
    /// Managed-cluster contexts routinely carry the meaningful half in the
    /// cluster name rather than the context name, so both are considered.
    /// </summary>
    [Test]
    public async Task Considers_the_cluster_name_too()
    {
        await Assert.That(ClusterEnvironments.Classify("cluster-7", "payments-prod"))
            .IsEqualTo(ClusterEnvironment.Production);
        await Assert.That(ClusterEnvironments.Classify(null, "acme-staging"))
            .IsEqualTo(ClusterEnvironment.Staging);
    }

    /// <summary>Production wins when a name claims two environments — bias toward the safe read.</summary>
    [Test]
    public async Task Production_wins_a_tie() =>
        await Assert.That(ClusterEnvironments.Classify("dev-tools", "prod-eu"))
            .IsEqualTo(ClusterEnvironment.Production);

    [Test]
    public async Task Unknown_carries_no_label()
    {
        await Assert.That(ClusterEnvironment.Unknown.Label()).IsNull();
        await Assert.That(ClusterEnvironment.Production.Label()).IsEqualTo("PROD");
    }
}
