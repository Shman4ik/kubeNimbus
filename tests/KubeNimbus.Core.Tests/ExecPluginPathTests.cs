using k8s.KubeConfigModels;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// FEAT-49's resolution rules, pure: the PATH, PATHEXT and platform are passed in, so the
/// Windows rules are asserted on Linux and the other way round. The end-to-end connect
/// through a resolved bare command is in <see cref="ExecPluginAuthTests"/>.
/// </summary>
public class ExecPluginPathTests
{
    [Test]
    public async Task A_bare_command_missing_from_path_is_found_in_a_login_shell_directory()
    {
        var (brew, _) = Tool("aws");

        var resolved = ExecPluginPath.Resolve("aws", null, "/usr/bin:/bin", null, windows: false, [brew]);

        await Assert.That(resolved).IsEqualTo(Path.Combine(brew, "aws"));
    }

    [Test]
    public async Task On_windows_a_bare_command_resolves_through_pathext_and_one_with_an_extension_as_itself()
    {
        var (directory, _) = Tool("kubelogin.exe");
        var (cmdDirectory, _) = Tool("az.cmd");

        var bare = ExecPluginPath.Resolve("kubelogin", null, directory, ".COM;.EXE;.BAT;.CMD", windows: true, []);
        var withExtension = ExecPluginPath.Resolve("kubelogin.exe", null, directory, ".COM;.EXE;.BAT;.CMD", windows: true, []);
        var wrapper = ExecPluginPath.Resolve("az", null, cmdDirectory, ".COM;.EXE;.BAT;.CMD", windows: true, []);

        // Case as PATHEXT spells it; Windows file names are case-insensitive.
        await Assert.That(bare!.ToLowerInvariant()).IsEqualTo(Path.Combine(directory, "kubelogin.exe").ToLowerInvariant());
        await Assert.That(withExtension!.ToLowerInvariant()).IsEqualTo(Path.Combine(directory, "kubelogin.exe").ToLowerInvariant());

        // A .cmd wrapper is exactly what CreateProcess would not find from a bare name.
        await Assert.That(wrapper!.ToLowerInvariant()).IsEqualTo(Path.Combine(cmdDirectory, "az.cmd").ToLowerInvariant());
    }

    [Test]
    public async Task An_absolute_command_is_left_alone_and_a_missing_one_is_not_invented()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "some", "plugin");

        await Assert.That(ExecPluginPath.Resolve(absolute, null, "", null, OperatingSystem.IsWindows(), [])).IsNull();
        await Assert.That(ExecPluginPath.Resolve("kubenimbus-nowhere", null, "", null, false, [])).IsNull();
        await Assert.That(ExecPluginPath.Resolve("", null, "", null, false, [])).IsNull();
    }

    [Test]
    public async Task A_relative_command_with_a_separator_resolves_against_the_kubeconfigs_folder()
    {
        // client-go's rule: exec.command containing a path separator is relative to the
        // kubeconfig file, not to wherever the app was started.
        var kubeconfigDirectory = Path.Combine(Path.GetTempPath(), "kubenimbus-kc");

        var resolved = ExecPluginPath.Resolve("bin/plugin", kubeconfigDirectory, "", null, OperatingSystem.IsWindows(), []);

        await Assert.That(resolved).IsEqualTo(Path.GetFullPath(Path.Combine(kubeconfigDirectory, "bin/plugin")));
    }

    [Test]
    public async Task The_plugins_own_path_gains_the_missing_directories_unless_the_kubeconfig_sets_it()
    {
        // gke-gcloud-auth-plugin runs gcloud, kubelogin's CLI mode runs az: finding the
        // plugin and not its tool would move the bug one level down.
        var (extra, _) = Tool("gcloud");
        using var _ = ExecPluginPath.OverrideDirectories([extra]);

        var plain = new ExternalExecution { Command = "gke-gcloud-auth-plugin" };
        var explicitPath = new ExternalExecution
        {
            Command = "gke-gcloud-auth-plugin",
            EnvironmentVariables = [new Dictionary<string, string> { ["name"] = "PATH", ["value"] = "/mine" }],
        };

        var added = ExecPluginPath.Apply(plain, null).AddedToPath;
        ExecPluginPath.Apply(explicitPath, null);

        await Assert.That(added).Contains(extra);
        await Assert.That(plain.EnvironmentVariables!.Single()["value"]).EndsWith(extra);
        await Assert.That(explicitPath.EnvironmentVariables!.Single()["value"]).IsEqualTo("/mine");
    }

    [Test]
    public async Task A_directory_already_on_path_or_absent_is_not_added()
    {
        var (present, _) = Tool("x");
        var absent = Path.Combine(Path.GetTempPath(), $"kubenimbus-absent-{Guid.NewGuid():N}");

        var missing = ExecPluginPath.MissingDirectories(present, OperatingSystem.IsWindows(), [present, absent]);

        await Assert.That(missing).IsEmpty();
    }

    private static (string Directory, string File) Tool(string name)
    {
        var directory = Directory.CreateTempSubdirectory("kubenimbus-tool").FullName;
        var file = Path.Combine(directory, name);
        File.WriteAllText(file, "");
        return (directory, file);
    }
}
