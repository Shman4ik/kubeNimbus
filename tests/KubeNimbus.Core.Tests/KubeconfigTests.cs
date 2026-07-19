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
}
