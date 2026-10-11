using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// ENG-39: where the app's own files go must never be a relative path. With a fresh
/// Linux HOME, <c>GetFolderPath</c> returned "" and the discovery cache was written to
/// <c>./kubeNimbus/discovery</c> of whatever directory the app was started from.
/// </summary>
public class AppDataDirectoryTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "kubenimbus-appdata-test");
    private static readonly string Temp = Path.Combine(Root, "tmp");

    /// <summary>Stands in for <see cref="AppDataDirectory.ProcessFallback"/>, which creates a directory.</summary>
    private static string Fallback() => Temp;

    [Test]
    public async Task The_platform_answer_wins_when_it_is_absolute()
    {
        var special = Path.Combine(Root, "special");

        await Assert.That(AppDataDirectory.Choose(special, Path.Combine(Root, "xdg"), Root, ".config", Fallback)).IsEqualTo(special);
    }

    [Test]
    public async Task An_empty_platform_answer_falls_back_to_xdg_then_home_then_temp()
    {
        var xdg = Path.Combine(Root, "xdg");

        await Assert.That(AppDataDirectory.Choose("", xdg, Root, ".config", Fallback)).IsEqualTo(xdg);
        await Assert.That(AppDataDirectory.Choose("", null, Root, ".config", Fallback)).IsEqualTo(Path.Combine(Root, ".config"));
        await Assert.That(AppDataDirectory.Choose("", null, "", ".config", Fallback)).IsEqualTo(Temp);
    }

    [Test]
    public async Task A_relative_value_anywhere_is_skipped_never_used()
    {
        // The XDG spec: a relative $XDG_CONFIG_HOME is invalid and must be ignored. A
        // relative HOME is no better. Either would put files in the current directory.
        var chosen = AppDataDirectory.Choose("kubeNimbus", "relative/xdg", "relative-home", ".config", Fallback);

        await Assert.That(chosen).IsEqualTo(Temp);
        await Assert.That(Path.IsPathFullyQualified(chosen)).IsTrue();
    }

    /// <summary>
    /// B4-2: the fallback used to be <c>/tmp/kubeNimbus-fallback</c>, a name any other local
    /// user could create first and fill with a settings file pointing at their kubeconfig.
    /// It is a directory this process created, with a random name, and every caller in the
    /// process gets the same one, so Roaming and Local do not split.
    /// </summary>
    [Test]
    public async Task The_fallback_is_a_fresh_random_directory_that_stays_put_for_the_process()
    {
        var first = AppDataDirectory.ProcessFallback;
        var second = AppDataDirectory.ProcessFallback;

        await Assert.That(second).IsEqualTo(first);
        await Assert.That(Directory.Exists(first)).IsTrue();
        await Assert.That(Path.IsPathFullyQualified(first)).IsTrue();
        await Assert.That(Path.GetFileName(first)).StartsWith("kubeNimbus-");
        await Assert.That(Path.GetFileName(first)).IsNotEqualTo("kubeNimbus-fallback");
        await Assert.That(Path.GetFileName(first).Length).IsGreaterThan("kubeNimbus-".Length);
        await Assert.That(Path.GetFullPath(Path.GetDirectoryName(first)!).TrimEnd(Path.DirectorySeparatorChar))
            .IsEqualTo(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar));

        if (!OperatingSystem.IsWindows())
        {
            await Assert.That(File.GetUnixFileMode(first))
                .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // With no usable home, the decision hands back exactly that directory — once per
        // call, never a second one.
        var calls = 0;
        string Counting() { calls++; return AppDataDirectory.ProcessFallback; }
        await Assert.That(AppDataDirectory.Choose("", null, "", ".config", Counting)).IsEqualTo(first);
        await Assert.That(AppDataDirectory.Choose("", null, "", Path.Combine(".local", "share"), Counting)).IsEqualTo(first);
        await Assert.That(calls).IsEqualTo(2);
    }

    /// <summary>The fallback is not created when a real answer exists: no stray temp directories.</summary>
    [Test]
    public async Task The_fallback_is_not_consulted_when_a_home_resolves()
    {
        var consulted = false;
        string Tracking() { consulted = true; return Temp; }

        await Assert.That(AppDataDirectory.Choose("", null, Root, ".config", Tracking)).IsEqualTo(Path.Combine(Root, ".config"));
        await Assert.That(consulted).IsFalse();
    }

    [Test]
    public async Task Every_store_resolves_to_an_absolute_path()
    {
        await Assert.That(Path.IsPathFullyQualified(AppDataDirectory.Roaming)).IsTrue();
        await Assert.That(Path.IsPathFullyQualified(AppDataDirectory.Local)).IsTrue();
        await Assert.That(Path.IsPathFullyQualified(TerminalLauncher.StateDirectory) || TerminalLauncher.DirectoryOverride is not null).IsTrue();
    }

    /// <summary>
    /// #289: with an isolated profile (<c>KUBENIMBUS_PROFILE_DIR</c>, what qa-app.ps1 sets),
    /// "Open a terminal on this cluster" wrote its overlay kubeconfig into the developer's
    /// real <c>%APPDATA%\kubeNimbus\terminal</c>, because only the two stores followed the
    /// override. Everything under the app-data directories follows it now: the terminal's
    /// overlay and hand-off files are written inside the profile and nowhere else, and the
    /// discovery cache's directory (<see cref="AppDataDirectory.Local"/>) is the profile too.
    /// Nothing is started: the lookup resolves no terminal.
    /// </summary>
    [Test]
    [NotInParallel(nameof(TerminalLauncher.DirectoryOverride))]
    public async Task An_isolated_profile_holds_every_file_the_app_writes_for_itself()
    {
        var realRoaming = AppDataDirectory.Roaming;
        var realLocal = AppDataDirectory.Local;
        var profile = Directory.CreateTempSubdirectory("kubenimbus-profile").FullName;
        var outside = Directory.CreateTempSubdirectory("kubenimbus-profile-outside").FullName;
        var userKubeconfig = Path.Combine(outside, "kubeconfig");
        var contextName = "qa-" + Guid.NewGuid().ToString("n");

        var previousTerminal = TerminalLauncher.DirectoryOverride;
        TerminalLauncher.DirectoryOverride = null;
        AppDataDirectory.ProfileOverride = profile;
        TerminalLauncher.KubectlProbeOverride.Value = () => Path.Combine(outside, "kubectl");
        TerminalLauncher.LookupOverride.Value = new TerminalLookup("", null, Path.Combine(outside, "no-system32"), OperatingSystem.IsWindows())
        {
            ApplicationDirectories = [],
        };
        try
        {
            await Assert.That(AppDataDirectory.Roaming).IsEqualTo(profile);
            await Assert.That(AppDataDirectory.Local).IsEqualTo(profile);
            await Assert.That(TerminalLauncher.StateDirectory).IsEqualTo(Path.Combine(profile, "terminal"));

            var context = new ClusterContext(contextName, "payments", "default", "dev", userKubeconfig);
            var command = TerminalCommand.Exec("payments", "api-0", "app", TerminalCommand.ShellFor(PodOperatingSystem.Unknown, null));
            var result = await TerminalLauncher.OpenAsync(context, command);

            await Assert.That(result.Outcome).IsEqualTo(TerminalLaunchOutcome.NoTerminal);

            // The overlay was written, inside the profile.
            var written = Directory.GetFiles(profile, "*", SearchOption.AllDirectories);
            await Assert.That(written.Length).IsGreaterThan(0);

            // Every path KUBECONFIG names is the overlay in the profile or the user's own file.
            foreach (var path in result.KubeconfigValue.Split(Path.PathSeparator))
            {
                await Assert.That(path == userKubeconfig || path.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.Ordinal)).IsTrue();
            }

            // And nothing landed in the real app-data directories, nor beside the user's kubeconfig.
            foreach (var real in new[] { realRoaming, realLocal })
            {
                var terminal = Path.Combine(real, "terminal");
                var leaked = Directory.Exists(terminal)
                    && Directory.EnumerateFiles(terminal).Any(f => written.Any(w => Path.GetFileName(w) == Path.GetFileName(f)));
                await Assert.That(leaked).IsFalse();
            }

            await Assert.That(Directory.GetFiles(outside, "*", SearchOption.AllDirectories)).IsEmpty();
        }
        finally
        {
            AppDataDirectory.ProfileOverride = null;
            TerminalLauncher.DirectoryOverride = previousTerminal;
            TerminalLauncher.KubectlProbeOverride.Value = null;
            TerminalLauncher.LookupOverride.Value = null;
        }

        await Assert.That(AppDataDirectory.Roaming).IsEqualTo(realRoaming);
    }

    // ------------------------------------------------- S1-4: private and atomic writes

    [Test]
    public async Task An_atomic_write_replaces_the_file_whole_and_leaves_nothing_beside_it()
    {
        var directory = Path.Combine(Directory.CreateTempSubdirectory("kubenimbus-atomic").FullName, "kubeNimbus");
        var path = Path.Combine(directory, "settings.json");

        AppDataDirectory.WriteAllTextAtomically(path, "first");
        AppDataDirectory.WriteAllTextAtomically(path, "second, longer than the first");

        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("second, longer than the first");
        await Assert.That(Directory.GetFiles(directory).Select(f => Path.GetFileName(f))).IsEquivalentTo(["settings.json"]);
    }

    [Test]
    public async Task The_settings_store_writes_through_the_atomic_path()
    {
        var directory = Directory.CreateTempSubdirectory("kubenimbus-atomic-settings").FullName;
        var store = new KubeNimbus.Core.Settings.AppSettingsStore(Path.Combine(directory, "settings.json"));

        store.Save(new KubeNimbus.Core.Settings.AppSettings());

        await Assert.That(store.Exists()).IsTrue();
        await Assert.That(Directory.GetFiles(directory).Select(f => Path.GetFileName(f))).IsEquivalentTo(["settings.json"]);
    }

    [Test]
    public async Task On_linux_and_macos_the_files_are_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows has no Unix modes; the per-user AppData folders are private to their user.
            Skip.Test("Unix file modes do not exist on Windows.");
            return;
        }

        var root = Directory.CreateTempSubdirectory("kubenimbus-private").FullName;
        var directory = Path.Combine(root, "kubeNimbus");
        var file = Path.Combine(directory, "workspace.json");

        AppDataDirectory.WriteAllTextAtomically(file, "{}");

        await Assert.That(File.GetUnixFileMode(directory)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await Assert.That(File.GetUnixFileMode(file)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task On_linux_and_macos_an_existing_wider_directory_is_tightened()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test("Unix file modes do not exist on Windows.");
            return;
        }

        var directory = Directory.CreateTempSubdirectory("kubenimbus-widen").FullName;
        File.SetUnixFileMode(directory, (UnixFileMode)0b111_101_101); // 0755, what umask 022 gave

        AppDataDirectory.CreatePrivate(directory);

        await Assert.That(File.GetUnixFileMode(directory)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
