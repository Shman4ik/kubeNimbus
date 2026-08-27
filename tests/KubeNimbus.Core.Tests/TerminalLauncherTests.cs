using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// The decisions behind "open a terminal on this cluster", asserted without a terminal.
///
/// <para>
/// Launching one is not testable here and is not faked: no CI runner and none of this
/// repo's containers has a terminal emulator, a Windows shell or a macOS
/// <c>LaunchServices</c>. What <em>is</em> testable is everything that decides what a
/// terminal would be handed — the overlay kubeconfig's contents, the <c>KUBECONFIG</c>
/// value, the per-platform candidate order, the PATH search behind the kubectl probe —
/// and those are where this feature can be silently wrong. A context that is not
/// actually pinned, or an overlay shared by two clusters, opens a terminal that looks
/// right and is aimed at the wrong cluster.
/// </para>
/// </summary>
public class TerminalLauncherTests
{
    private const string Home = "/home/dev/.kube/config";
    private const string State = "/state";

    private static TerminalLaunchPlan Plan(
        TerminalHostPlatform platform, string context = "payments-prod", string? terminal = null) =>
        TerminalLauncher.Plan(platform, context, Home, State, terminal);

    // -------------------------------------------------------- the context overlay

    /// <summary>
    /// The overlay is the whole mechanism for pinning a context without writing to the
    /// user's kubeconfig, and it must carry a name and nothing else — no cluster block,
    /// no user block, and therefore no token, certificate or exec-plugin invocation
    /// (hard rule 4).
    /// </summary>
    [Test]
    public async Task TheOverlayPinsTheContextAndCarriesNothingElse()
    {
        var overlay = TerminalLauncher.ContextOverlay("payments-prod");

        await Assert.That(overlay).Contains("apiVersion: v1");
        await Assert.That(overlay).Contains("kind: Config");
        await Assert.That(overlay).Contains("current-context: \"payments-prod\"");
        await Assert.That(overlay).DoesNotContain("clusters:");
        await Assert.That(overlay).DoesNotContain("users:");
        await Assert.That(overlay).DoesNotContain("client-certificate");
        await Assert.That(overlay).DoesNotContain("token");
    }

    /// <summary>
    /// Managed clusters hand out ARNs and URLs as context names, and a bare YAML scalar
    /// containing a colon-space or a leading brace is a parse error or, worse, a map.
    /// Quoted-and-escaped is the only safe form.
    /// </summary>
    [Test]
    [Arguments("arn:aws:eks:us-east-1:481516234298:cluster/search-staging")]
    [Arguments("gke_my-project_europe-west1_prod")]
    [Arguments("a name with spaces")]
    public async Task ContextNamesThatAreNotIdentifiersAreQuoted(string name)
    {
        await Assert.That(TerminalLauncher.ContextOverlay(name)).Contains($"current-context: \"{name}\"");
    }

    [Test]
    public async Task QuotesAndBackslashesInAContextNameAreEscaped()
    {
        var overlay = TerminalLauncher.ContextOverlay("""odd"name\here""");

        await Assert.That(overlay).Contains("""current-context: "odd\"name\\here" """.TrimEnd());
    }

    // ----------------------------------------------------------- the KUBECONFIG value

    /// <summary>
    /// The overlay has to come <em>first</em>: kubectl takes <c>current-context</c> from
    /// the first file in the chain that sets one, so an overlay appended after the real
    /// kubeconfig would be silently ignored and the terminal would open on whatever
    /// context the file happens to name.
    /// </summary>
    [Test]
    public async Task TheOverlayComesFirstInTheChainAndTheRealFileSecond()
    {
        var plan = Plan(TerminalHostPlatform.Linux);

        await Assert.That(plan.KubeconfigValue).IsEqualTo($"{plan.OverlayPath}:{Home}");
        await Assert.That(plan.KubeconfigValue.IndexOf(plan.OverlayPath, StringComparison.Ordinal)).IsEqualTo(0);
    }

    /// <summary>Windows separates KUBECONFIG entries with ';', not ':' — a path there starts "C:".</summary>
    [Test]
    public async Task WindowsUsesTheSemicolonSeparator()
    {
        var plan = TerminalLauncher.Plan(
            TerminalHostPlatform.Windows, "payments-prod", @"C:\Users\dev\.kube\config", @"C:\state");

        await Assert.That(plan.KubeconfigValue).IsEqualTo($@"{plan.OverlayPath};C:\Users\dev\.kube\config");
    }

    /// <summary>
    /// One overlay per context, never one shared file. Two terminals open on two
    /// clusters that shared a file would silently re-point the first at the second on
    /// its next command — the wrong-context incident the environment colours exist for.
    /// </summary>
    [Test]
    public async Task EachContextGetsItsOwnOverlayFileAndTheNameIsStable()
    {
        var first = Plan(TerminalHostPlatform.Linux, "payments-prod").OverlayPath;
        var second = Plan(TerminalHostPlatform.Linux, "payments-staging").OverlayPath;

        await Assert.That(first).IsNotEqualTo(second);
        await Assert.That(Plan(TerminalHostPlatform.Linux, "payments-prod").OverlayPath).IsEqualTo(first);
    }

    /// <summary>
    /// The file name is derived, not sanitized: real context names contain <c>/</c>,
    /// <c>:</c> and <c>\</c>, and any scheme that stripped those would map two different
    /// clusters onto one overlay.
    /// </summary>
    [Test]
    public async Task OverlayFileNamesAreFilesystemSafeForArnStyleContexts()
    {
        var plan = Plan(TerminalHostPlatform.Linux, "arn:aws:eks:us-east-1:1:cluster/search-staging");
        var fileName = Path.GetFileName(plan.OverlayPath);

        await Assert.That(fileName).DoesNotContain(":");
        await Assert.That(fileName).DoesNotContain("/");
        await Assert.That(fileName).DoesNotContain("\\");
        // Path.Combine joins with the host's separator, so "/state" plus a file name reads
        // back as "\state" on Windows. The claim here is which directory the overlay lands
        // in, not which slash the host spells it with.
        await Assert.That(Path.GetDirectoryName(plan.OverlayPath)?.Replace('\\', '/')).IsEqualTo(State);
    }

    // -------------------------------------------------------------- the candidates

    /// <summary>
    /// <c>wt.exe</c> is deliberately absent. Windows Terminal makes a new tab in an
    /// already-running process, which spawns the shell with <em>its</em> environment;
    /// the tab would look right and be pointed at the wrong cluster. Starting a shell
    /// directly still lands inside Windows Terminal wherever it is the default terminal
    /// application, because that is a console-host setting rather than a command line.
    /// </summary>
    [Test]
    public async Task WindowsStartsAShellDirectlyAndNeverGoesThroughWindowsTerminal()
    {
        var executables = Plan(TerminalHostPlatform.Windows).Candidates.Select(c => c.Executable).ToList();

        await Assert.That(executables).IsEquivalentTo(new[] { "pwsh.exe", "powershell.exe", "cmd.exe" });
        await Assert.That(executables.Any(e => e.Contains("wt", StringComparison.OrdinalIgnoreCase))).IsFalse();
    }

    /// <summary>
    /// macOS goes through a generated script because <c>open</c> hands the request to
    /// LaunchServices, which starts Terminal.app with the session's environment and not
    /// ours — a bare <c>open -a Terminal</c> would open a terminal on no cluster at all.
    /// </summary>
    [Test]
    public async Task MacOsOpensTerminalOnAScriptThatExportsTheEnvironment()
    {
        var plan = Plan(TerminalHostPlatform.MacOs);

        await Assert.That(plan.LauncherScriptPath).IsNotNull();
        await Assert.That(plan.Candidates).Count().IsEqualTo(1);
        await Assert.That(plan.Candidates[0].Executable).IsEqualTo("open");
        await Assert.That(plan.Candidates[0].Arguments)
            .IsEquivalentTo(new[] { "-a", "Terminal", plan.LauncherScriptPath! });

        var script = plan.LauncherScriptContent!;
        await Assert.That(script).StartsWith("#!/bin/sh");
        await Assert.That(script).Contains($"KUBECONFIG='{plan.KubeconfigValue}'");
        await Assert.That(script).Contains("export KUBECONFIG");
        await Assert.That(script).Contains("""exec "$SHELL" -l""");
    }

    /// <summary>A path with an apostrophe in it must not end the shell string early.</summary>
    [Test]
    public async Task TheLauncherScriptSingleQuoteEscapes()
    {
        await Assert.That(TerminalLauncher.LauncherScript("/home/o'brien/config"))
            .Contains("""KUBECONFIG='/home/o'\''brien/config'""");
    }

    /// <summary>
    /// The freedesktop launcher first (it honours the user's own default), then Debian's
    /// alternatives symlink, then the emulators. All started with no arguments, which
    /// every one of them reads as "open my default shell" and which is the only form
    /// that needs no per-emulator flag table.
    /// </summary>
    [Test]
    public async Task LinuxTriesTheStandardLaunchersBeforeProbingEmulators()
    {
        var candidates = Plan(TerminalHostPlatform.Linux).Candidates;

        await Assert.That(candidates[0].Executable).IsEqualTo("xdg-terminal-exec");
        await Assert.That(candidates[1].Executable).IsEqualTo("x-terminal-emulator");
        await Assert.That(candidates.Select(c => c.Executable)).Contains("gnome-terminal");
        await Assert.That(candidates.Select(c => c.Executable)).Contains("konsole");
        await Assert.That(candidates.Select(c => c.Executable)).Contains("xterm");
        await Assert.That(candidates.All(c => c.Arguments.Count == 0)).IsTrue();
    }

    /// <summary>Someone who exported $TERMINAL has already answered this question.</summary>
    [Test]
    public async Task TerminalEnvironmentVariableWinsOnLinux()
    {
        var candidates = Plan(TerminalHostPlatform.Linux, terminal: "wezterm").Candidates;

        await Assert.That(candidates[0].Executable).IsEqualTo("wezterm");
    }

    /// <summary>
    /// …and is ignored on macOS, where the launch has to go through <c>open</c> for the
    /// environment to survive at all.
    /// </summary>
    [Test]
    public async Task TerminalEnvironmentVariableIsIgnoredOnMacOs()
    {
        var candidates = Plan(TerminalHostPlatform.MacOs, terminal: "wezterm").Candidates;

        await Assert.That(candidates.Select(c => c.Executable)).DoesNotContain("wezterm");
    }

    // ----------------------------------------------------------- the kubectl probe

    [Test]
    public async Task FindExecutableTakesTheFirstPathEntryThatHasIt()
    {
        var root = TempTree();
        var first = Path.Combine(root, "a");
        var second = Path.Combine(root, "b");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var kubectl = HostExecutable("kubectl");
        await File.WriteAllTextAsync(Path.Combine(second, kubectl), "");

        await Assert.That(TerminalLauncher.FindExecutable(
                "kubectl", HostPath(first, second), null, windows: HostIsWindows))
            .IsEqualTo(Path.Combine(second, kubectl)).IgnoringCase();

        await File.WriteAllTextAsync(Path.Combine(first, kubectl), "");
        await Assert.That(TerminalLauncher.FindExecutable(
                "kubectl", HostPath(first, second), null, windows: HostIsWindows))
            .IsEqualTo(Path.Combine(first, kubectl)).IgnoringCase();
    }

    [Test]
    public async Task FindExecutableReturnsNullWhenNothingHasIt()
    {
        var root = TempTree();

        await Assert.That(TerminalLauncher.FindExecutable("kubectl", root, null, windows: false)).IsNull();
    }

    /// <summary>
    /// A GUI process inherits a shorter PATH than a login shell — the same reason
    /// $KUBECONFIG never reaches an app launched from Explorer or the Dock — so the
    /// probe also looks where package managers put things. Reading a miss as "kubectl is
    /// not installed" without this would warn on most Homebrew machines.
    /// </summary>
    [Test]
    public async Task FindExecutableAlsoLooksInTheLoginShellDirectories()
    {
        var root = TempTree();
        var extra = Path.Combine(root, "opt");
        Directory.CreateDirectory(extra);
        await File.WriteAllTextAsync(Path.Combine(extra, "kubectl"), "");

        await Assert.That(TerminalLauncher.FindExecutable("kubectl", "", null, windows: false, [extra]))
            .IsEqualTo(Path.Combine(extra, "kubectl"));
    }

    /// <summary>
    /// On Windows the name on PATH carries no extension; PATHEXT supplies them. The
    /// extensions here are lower-case only because this test runs on a case-sensitive
    /// filesystem — Windows' own PATHEXT is upper-case and its filesystem does not care.
    /// </summary>
    [Test]
    public async Task FindExecutableAppliesPathExtOnWindows()
    {
        var root = TempTree();
        await File.WriteAllTextAsync(Path.Combine(root, "kubectl.exe"), "");

        await Assert.That(TerminalLauncher.FindExecutable("kubectl", root, ".com;.exe;.bat", windows: true))
            .IsEqualTo(Path.Combine(root, "kubectl.exe"));
        await Assert.That(TerminalLauncher.FindExecutable("kubectl", root, null, windows: false)).IsNull();
    }

    /// <summary>A PATH entry with illegal characters is common on Windows and must not throw.</summary>
    [Test]
    public async Task FindExecutableSurvivesAJunkPathEntry()
    {
        var root = TempTree();
        var kubectl = HostExecutable("kubectl");
        await File.WriteAllTextAsync(Path.Combine(root, kubectl), "");

        await Assert.That(TerminalLauncher.FindExecutable(
                "kubectl", HostPath("\0bad", root), null, windows: HostIsWindows))
            .IsEqualTo(Path.Combine(root, kubectl)).IgnoringCase();
    }

    /// <summary>Windows has no Homebrew; the extra directories are a Unix answer to a Unix problem.</summary>
    [Test]
    public async Task LoginShellDirectoriesAreUnixOnly()
    {
        await Assert.That(TerminalLauncher.LoginShellDirectories(TerminalHostPlatform.Windows)).IsEmpty();
        await Assert.That(TerminalLauncher.LoginShellDirectories(TerminalHostPlatform.MacOs))
            .Contains("/opt/homebrew/bin");
    }

    // ------------------------------------------------------------ the demo cluster

    /// <summary>
    /// The demo cluster's objects ship inside the binary and its kubeconfig path is a
    /// sentinel that is not a path at all. It refuses before anything is written or
    /// started — never a terminal pointed at "&lt;demo&gt;" (the demo section's rule 5).
    /// </summary>
    [Test]
    public async Task TheDemoClusterRefusesAndTouchesNothing()
    {
        var directory = TempTree();
        var previous = TerminalLauncher.DirectoryOverride;
        TerminalLauncher.DirectoryOverride = directory;
        try
        {
            var result = await TerminalLauncher.OpenAsync(ClusterContext.Demo);

            await Assert.That(result.Outcome).IsEqualTo(TerminalLaunchOutcome.NoKubeconfig);
            await Assert.That(result.KubeconfigValue).IsEmpty();
            await Assert.That(result.KubectlMissing).IsFalse();
            await Assert.That(Directory.Exists(Path.Combine(directory, "terminal"))).IsFalse();
        }
        finally
        {
            TerminalLauncher.DirectoryOverride = previous;
        }
    }

    /// <summary>
    /// The PATH separator this host's own rules use. <see cref="TerminalLauncher.FindExecutable"/>
    /// takes it from its <c>windows:</c> flag, and a Windows PATH entry (<c>C:\Users\…</c>)
    /// contains the POSIX one — so a test that hardcodes <c>:</c> while handing over real
    /// directories composes two pieces of junk on Windows and asserts against a mangled
    /// path. The tests below need directories that exist, because the probe is a
    /// <c>File.Exists</c>, so the directories are the host's and the rules under test are
    /// the host's too. The cross-platform half — that the Windows rules are decidable from
    /// Linux and vice versa — is covered by <see cref="FindExecutableAppliesPathExtOnWindows"/>,
    /// which needs one PATH entry and therefore no separator at all.
    /// </summary>
    private static readonly bool HostIsWindows = OperatingSystem.IsWindows();

    private static string HostPath(params string[] entries) =>
        string.Join(HostIsWindows ? ';' : ':', entries);

    /// <summary>
    /// On Windows the name on PATH carries no extension and PATHEXT supplies it, so a
    /// file the probe can actually find has to be written as one.
    /// </summary>
    private static string HostExecutable(string name) => HostIsWindows ? name + ".exe" : name;

    private static string TempTree()
    {
        var path = Path.Combine(Path.GetTempPath(), "kubenimbus-terminal-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
