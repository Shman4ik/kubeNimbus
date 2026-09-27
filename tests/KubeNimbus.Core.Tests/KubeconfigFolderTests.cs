using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// FEAT-57: a picked folder instead of a picked file. The folder is expanded on every
/// search, so a kubeconfig dropped into it later is found by the next rescan — and what is
/// kept in settings is still only a path.
/// </summary>
public class KubeconfigFolderTests
{
    [Test]
    public async Task A_picked_folder_contributes_its_kubeconfigs_in_name_order()
    {
        var folder = Folder();
        Write(folder, "b-cluster.yaml", "b-ctx");
        Write(folder, "a-cluster", "a-ctx");

        var candidates = Kubeconfig.CandidatePaths([folder]);

        await Assert.That(candidates[0].Path).IsEqualTo(folder);
        await Assert.That(candidates[0].IsFolder).IsTrue();
        await Assert.That(candidates[0].Source).IsEqualTo(Kubeconfig.PickedFolderSource);
        await Assert.That(candidates[1].Path).IsEqualTo(Path.Combine(folder, "a-cluster"));
        await Assert.That(candidates[2].Path).IsEqualTo(Path.Combine(folder, "b-cluster.yaml"));
        await Assert.That(candidates[1].Source).IsEqualTo(Kubeconfig.InPickedFolderSource);
        await Assert.That(Kubeconfig.DiscoverPaths([folder])).DoesNotContain(folder);
    }

    [Test]
    public async Task Files_that_do_not_say_they_are_kubeconfigs_are_not_offered_to_the_parser()
    {
        // ~/.kube is the folder people pick, and it holds kubectx's one-line state file,
        // lock files and dotfiles. Each would otherwise be a "could not read" on every rescan.
        var folder = Folder();
        Write(folder, "config", "real-ctx");
        await File.WriteAllTextAsync(Path.Combine(folder, "kubectx"), "previous-context\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "config.lock"), "");
        Write(folder, ".hidden", "hidden-ctx");
        Directory.CreateDirectory(Path.Combine(folder, "cache"));
        Write(Path.Combine(folder, "cache"), "nested", "nested-ctx");

        var failures = new List<KubeconfigReadFailure>();
        var contexts = await Kubeconfig.LoadContextsAsync(extraPaths: [folder], failures: failures);

        await Assert.That(contexts.Select(c => c.Name).Where(n => n.EndsWith("-ctx", StringComparison.Ordinal))).IsEquivalentTo(["real-ctx"]);
        await Assert.That(failures.Where(f => f.Path.StartsWith(folder, StringComparison.Ordinal))).IsEmpty();
    }

    [Test]
    public async Task A_file_added_to_the_folder_later_is_found_by_the_next_load_and_changes_the_fingerprint()
    {
        var folder = Folder();
        Write(folder, "first.yaml", "first-ctx");
        var before = Kubeconfig.ChainFingerprint([folder]);
        var firstLoad = await Kubeconfig.LoadContextsAsync(extraPaths: [folder]);

        Write(folder, "second.yaml", "second-ctx");
        var after = Kubeconfig.ChainFingerprint([folder]);
        var secondLoad = await Kubeconfig.LoadContextsAsync(extraPaths: [folder]);

        await Assert.That(firstLoad.Any(c => c.Name == "second-ctx")).IsFalse();
        await Assert.That(secondLoad.Any(c => c.Name == "second-ctx")).IsTrue();
        await Assert.That(after).IsNotEqualTo(before);
        await Assert.That(Kubeconfig.ChainFingerprint([folder])).IsEqualTo(after);
    }

    [Test]
    public async Task A_broken_kubeconfig_in_the_folder_is_reported_and_costs_only_itself()
    {
        var folder = Folder();
        Write(folder, "good.yaml", "good-ctx");
        await File.WriteAllTextAsync(Path.Combine(folder, "broken.yaml"), "apiVersion: v1\nkind: Config\nclusters: [ this is : not yaml\n");

        var failures = new List<KubeconfigReadFailure>();
        var contexts = await Kubeconfig.LoadContextsAsync(extraPaths: [folder], failures: failures);

        await Assert.That(contexts.Any(c => c.Name == "good-ctx")).IsTrue();
        await Assert.That(failures.Single(f => f.Path.StartsWith(folder, StringComparison.Ordinal)).Path).IsEqualTo(Path.Combine(folder, "broken.yaml"));
    }

    [Test]
    public async Task A_picked_folder_that_is_gone_is_reported_as_missing()
    {
        var gone = Path.Combine(Path.GetTempPath(), $"kubenimbus-gone-folder-{Guid.NewGuid():N}");

        var entry = Kubeconfig.CandidatePaths([gone]).Single(c => c.Path == gone);

        await Assert.That(entry.Exists).IsFalse();
        await Assert.That(entry.Source).IsEqualTo(Kubeconfig.PickedSource);
    }

    private static string Folder() => Directory.CreateTempSubdirectory("kubenimbus-kubeconfig-folder").FullName;

    private static void Write(string folder, string name, string context) =>
        File.WriteAllText(Path.Combine(folder, name), $"""
            apiVersion: v1
            kind: Config
            clusters:
            - name: {context}
              cluster:
                server: https://127.0.0.1:1
            contexts:
            - name: {context}
              context:
                cluster: {context}
                user: {context}
            users:
            - name: {context}
              user:
                token: not-a-credential
            """);
}
