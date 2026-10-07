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
        // PATHEXT in the case the files below are written in. Windows would match either
        // case; the CI runner's filesystem is Linux's and would not, which made this test
        // pass on Windows only. The Windows rule under test is the extension search.
        const string PathExt = ".com;.exe;.bat;.cmd";
        var (directory, _) = Tool("kubelogin.exe");
        var (cmdDirectory, _) = Tool("az.cmd");

        var bare = ExecPluginPath.Resolve("kubelogin", null, directory, PathExt, windows: true, []);
        var withExtension = ExecPluginPath.Resolve("kubelogin.exe", null, directory, PathExt, windows: true, []);
        var wrapper = ExecPluginPath.Resolve("az", null, cmdDirectory, PathExt, windows: true, []);

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
        await File.WriteAllTextAsync(Path.Combine(extra, HostExecutable("gke-gcloud-auth-plugin")), "");
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

    /// <summary>
    /// S1-3: a command found nowhere is refused before anything is started. Handed to
    /// <c>Process.Start</c> as a bare name, .NET would look for it in the app's folder and the
    /// current directory before PATH — so a file of that name left in whatever folder the app
    /// was started from would run with the user's credentials in its environment. Here such a
    /// file exists in the current directory, and the plugin is still not found.
    /// </summary>
    [Test]
    public async Task A_command_found_nowhere_is_refused_and_never_looked_up_in_the_current_directory()
    {
        var name = $"kubenimbus-cwd-plugin-{Guid.NewGuid():N}";
        var planted = Path.Combine(Environment.CurrentDirectory, HostExecutable(name));
        await File.WriteAllTextAsync(planted, "");
        try
        {
            using var _ = ExecPluginPath.OverrideDirectories([]);
            var exec = new ExternalExecution { Command = name };

            var failure = await Assert.ThrowsAsync<ExecCredentialException>(() =>
            {
                ExecPluginPath.Apply(exec, kubeconfigDirectory: null);
                return Task.CompletedTask;
            });

            await Assert.That(failure!.Command).IsEqualTo(name);
            await Assert.That(failure.Message).StartsWith("Could not run");
            await Assert.That(exec.Command).IsEqualTo(name);

            // The failure view reads it as a plugin that could not be started, with its advice.
            var report = ConnectionReport.Explain(failure, [], "https://example:6443", []);
            await Assert.That(report.Step).IsEqualTo(ConnectionReport.RunningPlugin);
            await Assert.That(report.Headline).Contains("could not be started");
        }
        finally
        {
            File.Delete(planted);
        }
    }

    private static string HostExecutable(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static (string Directory, string File) Tool(string name)
    {
        var directory = Directory.CreateTempSubdirectory("kubenimbus-tool").FullName;
        var file = Path.Combine(directory, name);
        File.WriteAllText(file, "");
        return (directory, file);
    }
}
