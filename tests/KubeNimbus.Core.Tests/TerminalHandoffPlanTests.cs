using System.Text;
using KubeNimbus.Core;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// FEAT-17's fourth argument shape and FEAT-19's preference, asserted on the plan and never by
/// starting anything: no test here opens a terminal (machine-terminal.md). What a terminal is
/// handed is where the hand-off can be silently wrong — a pod name split into two arguments, a
/// quote that ends a PowerShell literal early, a window that closes on kubectl's refusal before
/// it can be read, Windows Terminal started where its tab would not carry the cluster.
/// </summary>
public class TerminalHandoffPlanTests
{
    private const string Home = "/home/dev/.kube/config";
    private const string State = "/state";
    private const string Kubectl = "/usr/local/bin/kubectl";
    private const string WindowsKubectl = @"C:\Program Files\kubectl\kubectl.exe";

    private static readonly TerminalCommand Exec =
        TerminalCommand.Exec("payments", "api-0", "app", TerminalCommand.ShellFor(PodOperatingSystem.Unknown, null));

    private static TerminalLaunchPlan Plan(
        TerminalHostPlatform platform, TerminalCommand? command = null, string? configured = null, string? terminal = null,
        string kubectl = Kubectl) =>
        TerminalLauncher.Plan(
            platform, "payments-prod", Home, State, terminal,
            configuredTerminal: configured, command: command, kubectlPath: command is null ? null : kubectl);

    private static string Decode(IReadOnlyList<string> arguments) =>
        Encoding.Unicode.GetString(Convert.FromBase64String(arguments[^1]));

    // ---------------------------------------------------------------------- Windows

    /// <summary>
    /// The central one for Windows. PowerShell is handed the command as an encoded script —
    /// every argument a single-quoted literal, the pod name one argument, nothing for .NET's
    /// command-line quoting and PowerShell's parsing to disagree about — and -NoExit keeps the
    /// window open on whatever kubectl printed. cmd.exe is not offered: its quoting rules do
    /// not round-trip an argument list.
    /// </summary>
    [Test]
    public async Task WindowsHandsPowerShellAnEncodedScriptAndKeepsTheWindowOpen()
    {
        var candidates = Plan(TerminalHostPlatform.Windows, Exec, kubectl: WindowsKubectl).Candidates;

        await Assert.That(candidates.Select(c => c.Label)).IsEquivalentTo(new[] { "PowerShell 7", "Windows PowerShell" });
        foreach (var candidate in candidates)
        {
            await Assert.That(candidate.Arguments.Take(3)).IsEquivalentTo(new[] { "-NoLogo", "-NoExit", "-EncodedCommand" });
            await Assert.That(candidate.Arguments.Count).IsEqualTo(4);
            await Assert.That(candidate.Arguments[3].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')).IsTrue();
            await Assert.That(Decode(candidate.Arguments)).IsEqualTo(
                $"& '{WindowsKubectl}' 'exec' '-it' '-n' 'payments' 'api-0' '-c' 'app' '--' '/bin/sh' '-c' '{TerminalCommand.ShellProbe}'");
        }
    }

    /// <summary>
    /// A quote inside a literal is doubled — including the typographic single quotes
    /// PowerShell also reads as one, which a path like <c>C:\Users\O’Brien</c> carries.
    /// </summary>
    [Test]
    public async Task APowerShellLiteralDoublesEverySingleQuoteKind()
    {
        var script = TerminalLauncher.PowerShellScript([@"C:\Users\O'Brien\kubectl.exe", "a\u2019b", "c\u2018d\u201Ae\u201Bf"]);

        await Assert.That(script).IsEqualTo(
            "& 'C:\\Users\\O''Brien\\kubectl.exe' 'a\u2019\u2019b' 'c\u2018\u2018d\u201A\u201Ae\u201B\u201Bf'");
    }

    /// <summary>
    /// Windows Terminal opens a tab from a process of its own, which inherits that process's
    /// environment and not ours — so named as the preference it is refused with the reason,
    /// never started (the env-inheritance trap, machine-terminal.md rule 4).
    /// </summary>
    [Test]
    [Arguments("wt")]
    [Arguments("wt.exe")]
    [Arguments(@"C:\Users\dev\AppData\Local\Microsoft\WindowsApps\wt.exe")]
    public async Task WindowsTerminalAsThePreferenceIsRefusedNotStarted(string configured)
    {
        var lookup = new TerminalLookup("", null, null, Windows: true);
        var candidates = TerminalLauncher.Plan(
            TerminalHostPlatform.Windows, "c", Home, State, configuredTerminal: configured, lookup: lookup).Candidates;

        await Assert.That(candidates[0].Refusal).IsNotNull();
        await Assert.That(candidates[0].ResolvedPath).IsNull();
        await Assert.That(TerminalLauncher.TriedLabels(candidates)[0]).Contains("default terminal application");
    }

    /// <summary>
    /// A preferred shell that is PowerShell gets the command; any other is refused for a
    /// command (and still opens for a plain terminal), because only PowerShell's script shape
    /// is built here.
    /// </summary>
    [Test]
    public async Task OnlyAPreferredPowerShellIsHandedACommand()
    {
        var pwsh = Plan(TerminalHostPlatform.Windows, Exec, configured: @"C:\tools\pwsh.exe", kubectl: WindowsKubectl).Candidates[0];
        var nu = Plan(TerminalHostPlatform.Windows, Exec, configured: "nu.exe", kubectl: WindowsKubectl).Candidates[0];
        var nuPlain = Plan(TerminalHostPlatform.Windows, configured: "nu.exe").Candidates[0];

        await Assert.That(pwsh.Label).IsEqualTo(@"Preferred terminal (C:\tools\pwsh.exe)");
        await Assert.That(pwsh.Arguments[1]).IsEqualTo("-NoExit");
        await Assert.That(nu.Refusal).IsEqualTo("only PowerShell is handed a command");
        await Assert.That(nuPlain.Refusal).IsNull();
        await Assert.That(nuPlain.Arguments).IsEmpty();
    }

    // ------------------------------------------------------------------------ Linux

    /// <summary>
    /// Linux needs the per-emulator flag the plain shape avoided, and runs the command through
    /// <c>/bin/sh</c> from its positional arguments, so no word of it is ever parsed by a shell
    /// and the window holds a shell afterwards.
    /// </summary>
    [Test]
    public async Task LinuxPutsEachEmulatorsOwnFlagBeforeTheHeldCommand()
    {
        var candidates = Plan(TerminalHostPlatform.Linux, Exec).Candidates;
        string[] held = ["/bin/sh", "-c", TerminalLauncher.HoldScript, Kubectl, .. Exec.Arguments];

        await Assert.That(candidates.Single(c => c.Label == "gnome-terminal").Arguments).IsEquivalentTo(new[] { "--" }.Concat(held));
        await Assert.That(candidates.Single(c => c.Label == "konsole").Arguments).IsEquivalentTo(new[] { "-e" }.Concat(held));
        await Assert.That(candidates.Single(c => c.Label == "xfce4-terminal").Arguments).IsEquivalentTo(new[] { "-x" }.Concat(held));
        await Assert.That(candidates.Single(c => c.Label == "wezterm").Arguments).IsEquivalentTo(new[] { "start", "--" }.Concat(held));
        await Assert.That(candidates.Single(c => c.Label == "kitty").Arguments).IsEquivalentTo(held);
        await Assert.That(candidates.Single(c => c.Label == "xdg-terminal-exec").Arguments).IsEquivalentTo(held);
        await Assert.That(TerminalLauncher.HoldScript).IsEqualTo("\"$0\" \"$@\"; exec \"${SHELL:-/bin/sh}\"");
    }

    /// <summary>
    /// An emulator whose flag takes the command as one string is refused rather than given a
    /// string built here; for a plain terminal it is still tried with no arguments.
    /// </summary>
    [Test]
    public async Task AnEmulatorThatTakesOneStringIsRefusedForACommandOnly()
    {
        var command = Plan(TerminalHostPlatform.Linux, Exec).Candidates;
        var plain = Plan(TerminalHostPlatform.Linux).Candidates;

        await Assert.That(command.Single(c => c.Label == "tilix").Refusal).IsNotNull();
        await Assert.That(command.Single(c => c.Label == "lxterminal").Refusal).IsNotNull();
        await Assert.That(plain.All(c => c.Refusal is null && c.Arguments.Count == 0)).IsTrue();

        // Named as the preference, by name or path, it is refused the same way rather than given -e.
        var preferred = Plan(TerminalHostPlatform.Linux, Exec, configured: "/usr/bin/tilix").Candidates[0];
        await Assert.That(preferred.Label).IsEqualTo("Preferred terminal (/usr/bin/tilix)");
        await Assert.That(preferred.Refusal).IsNotNull();
        await Assert.That(Plan(TerminalHostPlatform.Linux, configured: "tilix").Candidates[0].Refusal).IsNull();
    }

    /// <summary>
    /// FEAT-19: the preference is tried first, then $TERMINAL, then the probe list; a preferred
    /// emulator the table does not know is given xterm's <c>-e</c>.
    /// </summary>
    [Test]
    public async Task ThePreferenceComesBeforeTerminalAndTheProbeList()
    {
        var plain = Plan(TerminalHostPlatform.Linux, configured: "cool-retro-term", terminal: "wezterm").Candidates;
        var command = Plan(TerminalHostPlatform.Linux, Exec, configured: "cool-retro-term").Candidates;

        await Assert.That(plain[0].Label).IsEqualTo("Preferred terminal (cool-retro-term)");
        await Assert.That(plain[1].Label).IsEqualTo("$TERMINAL (wezterm)");
        await Assert.That(plain[2].Label).IsEqualTo("xdg-terminal-exec");
        await Assert.That(command[0].Arguments[0]).IsEqualTo("-e");

        // The same value in both is tried once.
        var same = Plan(TerminalHostPlatform.Linux, configured: "kitty", terminal: "kitty").Candidates;
        await Assert.That(same.Count(c => c.Executable == "kitty")).IsEqualTo(2); // the preference, and the probe's own entry
        await Assert.That(same.Count(c => c.Label.StartsWith("$TERMINAL", StringComparison.Ordinal))).IsEqualTo(0);
    }

    /// <summary>A relative preference names the current directory and is refused like a relative $TERMINAL (B4-1).</summary>
    [Test]
    public async Task ARelativePreferenceIsNeverResolvedAgainstTheCurrentDirectory()
    {
        var lookup = new TerminalLookup("", null, null, OperatingSystem.IsWindows());
        var preferred = TerminalLauncher.Plan(
            TerminalHostPlatform.Linux, "c", Home, State, configuredTerminal: "./term", lookup: lookup).Candidates[0];

        await Assert.That(preferred.Source).IsEqualTo(TerminalExecutableSource.Absolute);
        await Assert.That(preferred.ResolvedPath).IsNull();
    }

    // ------------------------------------------------------------------------ macOS

    /// <summary>
    /// FEAT-19's macOS half: iTerm2 and Ghostty are tried before Terminal.app, each through
    /// /usr/bin/open on the launcher script that carries KUBECONFIG; Ghostty is given the
    /// script after -e, in a new instance, because it does not run a script it is asked to open.
    /// </summary>
    [Test]
    public async Task MacOsTriesITermAndGhosttyBeforeTerminal()
    {
        var plan = Plan(TerminalHostPlatform.MacOs);
        var script = plan.LauncherScriptPath!;

        await Assert.That(plan.Candidates.Select(c => c.Label)).IsEquivalentTo(new[] { "iTerm2", "Ghostty", "Terminal" });
        await Assert.That(plan.Candidates.All(c => c.Executable == "/usr/bin/open")).IsTrue();
        await Assert.That(plan.Candidates[0].Arguments).IsEquivalentTo(new[] { "-a", "iTerm", script });
        await Assert.That(plan.Candidates[1].Arguments).IsEquivalentTo(new[] { "-na", "Ghostty", "--args", "-e", script });
        await Assert.That(plan.Candidates[2].Arguments).IsEquivalentTo(new[] { "-a", "Terminal", script });
    }

    /// <summary>The preference is an application name on macOS, honoured where $TERMINAL is not, and never listed twice.</summary>
    [Test]
    public async Task MacOsTakesThePreferenceAsAnApplicationFirst()
    {
        var wezterm = Plan(TerminalHostPlatform.MacOs, configured: "WezTerm", terminal: "kitty").Candidates;
        var iterm = Plan(TerminalHostPlatform.MacOs, configured: "iTerm2").Candidates;

        await Assert.That(wezterm.Select(c => c.MacApplication ?? "")).IsEquivalentTo(new[] { "WezTerm", "iTerm", "Ghostty", "Terminal" });
        await Assert.That(wezterm[0].Label).IsEqualTo("Preferred terminal (WezTerm)");
        await Assert.That(iterm.Select(c => c.MacApplication ?? "")).IsEquivalentTo(new[] { "iTerm", "Ghostty", "Terminal" });
    }

    /// <summary>
    /// An application that is not installed is not tried: <c>open -a</c> on a missing app would
    /// otherwise be the one candidate standing between the user and Terminal.app.
    /// </summary>
    [Test]
    public async Task AMacApplicationThatIsNotInstalledDoesNotResolve()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kubenimbus-terminal-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(folder, "Terminal.app"));
        var lookup = new TerminalLookup("", null, null, Windows: false) { ApplicationDirectories = [folder] };

        // A candidate whose executable resolves, so only the bundle check is in play.
        var open = Path.Combine(folder, "open");
        await File.WriteAllTextAsync(open, "");
        TerminalCandidate Candidate(string application) =>
            new(open, [], application, TerminalExecutableSource.Absolute) { MacApplication = application };

        await Assert.That(TerminalLauncher.Resolve(Candidate("Terminal"), lookup).ResolvedPath).IsEqualTo(open);
        await Assert.That(TerminalLauncher.Resolve(Candidate("iTerm"), lookup).ResolvedPath).IsNull();
        await Assert.That(TerminalLauncher.Resolve(Candidate(Path.Combine(folder, "Terminal.app")), lookup).ResolvedPath).IsEqualTo(open);
    }

    /// <summary>
    /// On macOS a command goes into its own script, which exports KUBECONFIG, runs every word
    /// single-quoted, leaves a login shell behind, and removes itself as it starts — the
    /// shell's own <c>open-*.command</c> is left alone.
    /// </summary>
    [Test]
    public async Task MacOsWritesTheCommandIntoASelfRemovingScript()
    {
        var plain = Plan(TerminalHostPlatform.MacOs);
        var plan = Plan(TerminalHostPlatform.MacOs, Exec);
        var script = plan.LauncherScriptContent!;

        await Assert.That(Path.GetFileName(plan.LauncherScriptPath!)).StartsWith("run-");
        await Assert.That(plan.LauncherScriptPath).IsNotEqualTo(plain.LauncherScriptPath);
        await Assert.That(script).StartsWith("#!/bin/sh");
        await Assert.That(script).Contains("rm -f \"$0\"");
        await Assert.That(script).Contains($"KUBECONFIG='{plan.KubeconfigValue}'");
        await Assert.That(script).Contains(
            $"'{Kubectl}' 'exec' '-it' '-n' 'payments' 'api-0' '-c' 'app' '--' '/bin/sh' '-c' '{TerminalCommand.ShellProbe}'");
        await Assert.That(script).Contains("exec \"$SHELL\" -l");
        await Assert.That(plan.Candidates[0].Arguments[^1]).IsEqualTo(plan.LauncherScriptPath!);

        // The same command on the same cluster is the same file; another command is another.
        await Assert.That(Plan(TerminalHostPlatform.MacOs, Exec).LauncherScriptPath).IsEqualTo(plan.LauncherScriptPath);
        var other = TerminalCommand.NodeShell("worker-1", DebugContainers.DefaultImage);
        await Assert.That(Plan(TerminalHostPlatform.MacOs, other).LauncherScriptPath).IsNotEqualTo(plan.LauncherScriptPath);
    }

    [Test]
    public async Task ACommandWithoutTheKubectlThatRunsItIsAProgrammingError()
    {
        await Assert.That(() => TerminalLauncher.Plan(TerminalHostPlatform.Linux, "c", Home, State, command: Exec))
            .Throws<ArgumentException>();
    }

    // ------------------------------------------------------------- no kubectl blocks

    /// <summary>
    /// The central one for the launch. A plain terminal is worth opening without kubectl and
    /// only warns; a hand-off <em>is</em> a kubectl command, so with none found nothing is
    /// written and nothing is started, and the outcome says so. Nothing could be started here
    /// anyway — the lookup resolves nothing — so a broken block turns this red as NoTerminal,
    /// never as a window on the desk of whoever runs the suite.
    /// </summary>
    [Test]
    [NotInParallel(nameof(TerminalLauncher.DirectoryOverride))]
    public async Task AHandOffWithNoKubectlOpensNothingAndSaysWhy()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kubenimbus-terminal-tests", Guid.NewGuid().ToString("n"));
        var previous = TerminalLauncher.DirectoryOverride;
        TerminalLauncher.DirectoryOverride = directory;
        TerminalLauncher.KubectlProbeOverride.Value = () => null;
        TerminalLauncher.LookupOverride.Value = new TerminalLookup("", null, Path.Combine(directory, "no-system32"), OperatingSystem.IsWindows())
        {
            ApplicationDirectories = [],
        };
        try
        {
            var context = new ClusterContext("payments-prod", "payments", "default", "dev", Path.Combine(directory, "kubeconfig"));
            var result = await TerminalLauncher.OpenAsync(context, Exec);

            await Assert.That(result.Outcome).IsEqualTo(TerminalLaunchOutcome.NoKubectl);
            await Assert.That(result.Command).IsEqualTo(Exec.Summary);
            await Assert.That(Directory.Exists(directory)).IsFalse();
        }
        finally
        {
            TerminalLauncher.DirectoryOverride = previous;
            TerminalLauncher.KubectlProbeOverride.Value = null;
            TerminalLauncher.LookupOverride.Value = null;
        }
    }
}
