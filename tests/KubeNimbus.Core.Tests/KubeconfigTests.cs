using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

public class KubeconfigTests
{
    [Test]
    public async Task DiscoverPaths_includes_default_when_no_env()
    {
        // Sanity: discovery never throws and returns only existing files.
        var paths = Kubeconfig.DiscoverPaths();
        foreach (var p in paths)
        {
            await Assert.That(File.Exists(p)).IsTrue();
        }
    }

    [Test]
    public async Task LoadContexts_reads_sandbox_kubeconfig()
    {
        var path = SandboxCluster.KubeconfigPath;
        if (path is null)
        {
            return; // sandbox not provisioned; see CLAUDE.md
        }

        var contexts = await Kubeconfig.LoadContextsAsync([path]);

        await Assert.That(contexts).IsNotEmpty();
        await Assert.That(contexts[0].KubeconfigPath).IsEqualTo(path);
        await Assert.That(contexts[0].ClusterName).IsNotEmpty();
    }

    // ------------------------------------------------- user-picked extra paths
    //
    // "Open kubeconfig file…" hands the picked path back through this API on every
    // load — nothing is copied, so these are the semantics the whole feature rests on.

    [Test]
    public async Task CandidatePaths_reports_a_picked_file_with_its_own_source()
    {
        var picked = WriteKubeconfig("picked-ctx");
        try
        {
            var candidates = Kubeconfig.CandidatePaths([picked]);
            var entry = candidates.FirstOrDefault(c => c.Path == picked);

            await Assert.That(entry).IsNotNull();
            await Assert.That(entry!.Exists).IsTrue();
            await Assert.That(entry.Source).IsEqualTo(Kubeconfig.PickedSource);

            // First, so an explicit choice outranks $KUBECONFIG on a duplicate name.
            await Assert.That(candidates[0].Path).IsEqualTo(picked);
        }
        finally
        {
            File.Delete(picked);
        }
    }

    [Test]
    public async Task CandidatePaths_reports_a_picked_file_that_no_longer_exists_as_missing()
    {
        // The whole reason CandidatePaths exists alongside DiscoverPaths: a pick that
        // has been moved or deleted has to stay *visible* in the empty state's search
        // list rather than silently disappearing.
        var gone = Path.Combine(Path.GetTempPath(), $"kubenimbus-gone-{Guid.NewGuid():N}.yaml");

        var entry = Kubeconfig.CandidatePaths([gone]).FirstOrDefault(c => c.Path == gone);

        await Assert.That(entry).IsNotNull();
        await Assert.That(entry!.Exists).IsFalse();
        await Assert.That(entry.Source).IsEqualTo(Kubeconfig.PickedSource);
        await Assert.That(Kubeconfig.DiscoverPaths([gone])).DoesNotContain(gone);
    }

    [Test]
    public async Task LoadContexts_includes_contexts_from_a_picked_file()
    {
        var picked = WriteKubeconfig("picked-ctx");
        try
        {
            var contexts = await Kubeconfig.LoadContextsAsync(extraPaths: [picked]);

            var match = contexts.FirstOrDefault(c => c.Name == "picked-ctx");
            await Assert.That(match).IsNotNull();
            await Assert.That(match!.KubeconfigPath).IsEqualTo(picked);
            await Assert.That(match.ClusterName).IsEqualTo("picked-cluster");
        }
        finally
        {
            File.Delete(picked);
        }
    }

    [Test]
    public async Task LoadContexts_degrades_rather_than_throwing_when_a_picked_file_is_gone()
    {
        var gone = Path.Combine(Path.GetTempPath(), $"kubenimbus-gone-{Guid.NewGuid():N}.yaml");

        // The empty state has to be reachable from a stale pick — an exception here
        // would leave the shell with no contexts *and* no way to explain why.
        var contexts = await Kubeconfig.LoadContextsAsync(extraPaths: [gone]);

        await Assert.That(contexts.Any(c => c.KubeconfigPath == gone)).IsFalse();
    }

    [Test]
    public async Task LoadContexts_ignores_extra_paths_when_an_explicit_list_is_given()
    {
        var explicitFile = WriteKubeconfig("explicit-ctx");
        var picked = WriteKubeconfig("picked-ctx");
        try
        {
            var contexts = await Kubeconfig.LoadContextsAsync([explicitFile], extraPaths: [picked]);

            await Assert.That(contexts.Any(c => c.Name == "explicit-ctx")).IsTrue();
            await Assert.That(contexts.Any(c => c.Name == "picked-ctx")).IsFalse();
        }
        finally
        {
            File.Delete(explicitFile);
            File.Delete(picked);
        }
    }

    // ------------------------------------------------- one unreadable file in the chain
    //
    // A five-entry $KUBECONFIG with one hand-edited file used to yield zero contexts:
    // the loader threw on that file and the shell caught once, for the whole chain.

    [Test]
    public async Task LoadContexts_skips_an_unparseable_file_and_names_it_when_asked()
    {
        var good = WriteKubeconfig("good-ctx");
        var broken = Path.Combine(Path.GetTempPath(), $"kubenimbus-broken-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(broken, "apiVersion: v1\nkind: Config\ncontexts: [ this is: not: yaml\n");
        try
        {
            var failures = new List<KubeconfigReadFailure>();
            var contexts = await Kubeconfig.LoadContextsAsync([broken, good], failures: failures);

            await Assert.That(contexts.Select(c => c.Name)).IsEquivalentTo(["good-ctx"]);
            await Assert.That(failures.Count).IsEqualTo(1);
            await Assert.That(failures[0].Path).IsEqualTo(broken);
            await Assert.That(failures[0].Message).IsNotEmpty();
            await Assert.That(failures[0].Message).DoesNotContain("\n");
        }
        finally
        {
            File.Delete(good);
            File.Delete(broken);
        }
    }

    [Test]
    public async Task LoadContexts_still_throws_on_an_unparseable_file_when_no_failure_list_is_given()
    {
        var broken = Path.Combine(Path.GetTempPath(), $"kubenimbus-broken-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(broken, "apiVersion: v1\nkind: Config\ncontexts: [ this is: not: yaml\n");
        try
        {
            await Assert.That(async () => await Kubeconfig.LoadContextsAsync([broken])).Throws<Exception>();
        }
        finally
        {
            File.Delete(broken);
        }
    }

    /// <summary>A minimal but real kubeconfig — enough for the client library's loader to parse.</summary>
    private static string WriteKubeconfig(string contextName)
    {
        var path = Path.Combine(Path.GetTempPath(), $"kubenimbus-test-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, $"""
            apiVersion: v1
            kind: Config
            current-context: {contextName}
            clusters:
            - name: picked-cluster
              cluster:
                server: https://127.0.0.1:6443
            users:
            - name: picked-user
              user:
                token: not-a-real-token
            contexts:
            - name: {contextName}
              context:
                cluster: picked-cluster
                user: picked-user
                namespace: picked-ns
            """);
        return path;
    }
}
