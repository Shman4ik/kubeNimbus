using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace KubeNimbus.Core;

/// <summary>Which host the terminal heuristics are being resolved for.</summary>
public enum TerminalHostPlatform
{
    Windows,
    MacOs,
    Linux,
}

/// <summary>Where a <see cref="TerminalCandidate"/>'s executable is looked for.</summary>
public enum TerminalExecutableSource
{
    /// <summary>A bare name, searched for on <c>PATH</c> (fully qualified entries only).</summary>
    Path,

    /// <summary>A path relative to the Windows system directory (<c>System32</c>).</summary>
    SystemDirectory,

    /// <summary>Already a path; used only if it is fully qualified and exists.</summary>
    Absolute,
}

/// <summary>One external-terminal invocation to try, in order.</summary>
/// <param name="Executable">
/// What to look for, read according to <paramref name="Source"/>. Never handed to the
/// process start as it is: see <see cref="TerminalLauncher.Resolve"/>.
/// </param>
/// <param name="Arguments">Passed as an argument list — never a concatenated command line.</param>
/// <param name="Label">What the UI calls it when it worked, or lists when nothing did.</param>
/// <param name="Source">How <paramref name="Executable"/> becomes an absolute path.</param>
public sealed record TerminalCandidate(
    string Executable,
    IReadOnlyList<string> Arguments,
    string Label,
    TerminalExecutableSource Source = TerminalExecutableSource.Path)
{
    /// <summary>
    /// The absolute path the candidate will be started as, once <see cref="TerminalLauncher.Resolve"/>
    /// has found it; null before resolution, and after it when nothing was found.
    /// </summary>
    public string? ResolvedPath { get; init; }

    /// <summary>
    /// On macOS, the application <c>open</c> is asked for (<c>iTerm</c>, <c>Ghostty</c>,
    /// <c>Terminal</c>, or what the preference names). With a lookup that lists application
    /// folders, a candidate whose <c>.app</c> is in none of them is not tried, so a machine without
    /// iTerm2 does not report an <c>open</c> that failed as a terminal that opened.
    /// </summary>
    public string? MacApplication { get; init; }

    /// <summary>
    /// Why this candidate is never started, when that is decided by the rules rather than by
    /// what is installed: Windows Terminal named as the preference, or an emulator that cannot
    /// be handed a command as an argument list. Listed with its reason when nothing opens.
    /// </summary>
    public string? Refusal { get; init; }
}

/// <summary>
/// What <see cref="TerminalLauncher.Resolve"/> searches: this process's <c>PATH</c> and
/// <c>PATHEXT</c> and the Windows system directory, passed in so the rules are testable with a
/// fake PATH and a fake <c>System32</c>.
/// </summary>
public sealed record TerminalLookup(string? PathValue, string? PathExt, string? SystemDirectory, bool Windows)
{
    /// <summary>
    /// macOS only: the folders an application bundle is looked for in. Null skips the check
    /// (and every other platform has none to make).
    /// </summary>
    public IReadOnlyList<string>? ApplicationDirectories { get; init; }

    /// <summary>The running process's own values.</summary>
    public static TerminalLookup Current => new(
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetEnvironmentVariable("PATHEXT"),
        OperatingSystem.IsWindows() ? Environment.SystemDirectory : null,
        OperatingSystem.IsWindows())
    {
        ApplicationDirectories = OperatingSystem.IsMacOS()
            ?
            [
                "/Applications",
                "/Applications/Utilities",
                "/System/Applications",
                "/System/Applications/Utilities",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"),
            ]
            : null,
    };
}

/// <summary>How <see cref="TerminalLauncher.OpenAsync"/> ended.</summary>
public enum TerminalLaunchOutcome
{
    /// <summary>A terminal was started.</summary>
    Opened,

    /// <summary>Nothing on the candidate list could be started.</summary>
    NoTerminal,

    /// <summary>There is no kubeconfig to point a terminal at (the demo cluster).</summary>
    NoKubeconfig,

    /// <summary>Something else went wrong — writing the overlay file, typically.</summary>
    Failed,

    /// <summary>
    /// A command was to be handed to the terminal and no <c>kubectl</c> could be found to run
    /// it. Unlike a plain terminal, which is useful without kubectl and only warns, a hand-off
    /// <em>is</em> a kubectl command, so nothing is opened.
    /// </summary>
    NoKubectl,
}

/// <summary>What happened, in enough detail for the UI to state it (UI rule 9).</summary>
public sealed record TerminalLaunchResult(
    TerminalLaunchOutcome Outcome,
    string? TerminalLabel,
    string? KubectlPath,
    string KubeconfigValue,
    string ContextName,
    IReadOnlyList<string> Tried,
    string? Error)
{
    /// <summary>
    /// A terminal opened but no <c>kubectl</c> could be found. Deliberately not a
    /// failure: see <see cref="TerminalLauncher.FindKubectl"/> for why the probe is
    /// weak evidence, and why the terminal is still worth having without it.
    /// </summary>
    public bool KubectlMissing => Outcome == TerminalLaunchOutcome.Opened && KubectlPath is null;

    /// <summary>The command handed to the terminal, as a person would type it; null for a plain terminal.</summary>
    public string? Command { get; init; }
}

/// <summary>
/// Everything <see cref="TerminalLauncher.OpenAsync"/> will do, computed before anything
/// is started. Split out so the parts that are decisions rather than side effects — the
/// overlay's contents, the <c>KUBECONFIG</c> value, the candidate order — are testable on
/// a machine with no terminal emulator on it (which is every CI runner this repo has).
/// </summary>
public sealed record TerminalLaunchPlan(
    string ContextName,
    string OverlayPath,
    string OverlayContent,
    string KubeconfigValue,
    IReadOnlyList<TerminalCandidate> Candidates,
    string? LauncherScriptPath,
    string? LauncherScriptContent)
{
    /// <summary>The command the terminal runs, or null for a plain terminal on the cluster.</summary>
    public TerminalCommand? Command { get; init; }
}

/// <summary>
/// Opens the machine's own terminal with <c>KUBECONFIG</c> set and the current context
/// pinned to one cluster — the daily gesture people leave a GUI for.
///
/// <para>
/// <b>Paths only, never credentials</b> (hard rule 4). The child process is handed the
/// <em>path</em> of the kubeconfig the context came from, exactly as the app itself
/// re-resolves it at connect time; nothing is copied, decoded or cached.
/// </para>
///
/// <para>
/// <b>Pinning the context without touching the user's kubeconfig.</b> kubectl has no
/// environment variable for "current context" — kubectx and friends work by rewriting
/// the file, which this app must not do (someone's shell, their other terminals and
/// their next kubeNimbus session would all silently move with it). What kubectl does
/// have is <c>KUBECONFIG</c> merging: files are merged left to right and
/// <c>current-context</c> comes from the <em>first</em> file that sets it. So the
/// launcher writes a one-key overlay — <c>apiVersion</c>, <c>kind</c> and
/// <c>current-context</c>, no clusters, no users, no credentials — and sets
/// <c>KUBECONFIG=&lt;overlay&gt;&lt;sep&gt;&lt;the real file&gt;</c>. The real file is
/// merged in unchanged and is never written to by kubeNimbus. Everything that reads a
/// kubeconfig — helm, k9s, stern, kubectx — gets the same answer, which an alias or a
/// shell function would not.
/// </para>
///
/// <para>
/// The "real file" is the single file that context was found in
/// (<see cref="ClusterContext.KubeconfigPath"/>) — exactly what
/// <c>Kubeconfig.BuildClientConfig</c> hands the in-app client. The terminal and the tab
/// it was launched from therefore resolve the same context out of the same file; passing
/// the whole discovered chain instead could resolve a duplicate context name differently
/// from the tab that opened it.
/// </para>
///
/// <para>
/// <b>One overlay per context, not one per launch.</b> Two terminals open on two
/// clusters must not share a file: rewriting a single overlay would silently re-point
/// the first terminal at the second cluster on its next command, which is precisely the
/// wrong-context incident the environment colours exist to prevent.
/// </para>
///
/// <para>
/// <b>Never pruned, deliberately (ENG-15).</b> Nothing removes an overlay, including one
/// for a context no kubeconfig has any more. The directory is bounded by the distinct
/// context names ever opened — about 60 bytes each, plus a short launcher script on macOS
/// — because re-opening a context rewrites its own files. The app cannot tell whether a
/// terminal it opened is still running, and deleting an overlay a live terminal names in
/// its <c>KUBECONFIG</c> is the one failure worse than a stale file: kubectl skips the
/// missing path and takes <c>current-context</c> from the real file instead, so that
/// terminal's next command runs against whichever cluster the user last switched to,
/// with nothing on screen to say so.
/// </para>
/// </summary>
public static class TerminalLauncher
{
    /// <summary>
    /// Overrides the directory the context overlays (and, on macOS, the launcher
    /// script) are written to. Same purpose as <c>AppSettingsStore.DirectoryOverride</c>:
    /// a test run must not write into the files of whoever is running it.
    /// </summary>
    public static string? DirectoryOverride { get; set; }

    /// <summary>
    /// How long a started candidate is given to prove it did not immediately die. A
    /// terminal emulator that cannot reach a display exits within milliseconds with a
    /// non-zero code; without this, the launcher would report "opened" over a window
    /// that never appeared.
    /// </summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMilliseconds(400);

    public static TerminalHostPlatform CurrentPlatform =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? TerminalHostPlatform.Windows
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? TerminalHostPlatform.MacOs
        : TerminalHostPlatform.Linux;

    /// <summary>Where the overlay (and the macOS launcher script) live.</summary>
    public static string StateDirectory => Path.Combine(
        DirectoryOverride ?? AppDataDirectory.Roaming,
        "terminal");

    /// <summary>
    /// The whole plan — overlay contents, <c>KUBECONFIG</c> value and the ordered
    /// candidate list — without starting anything or writing anything. Pure apart from
    /// reading <paramref name="preferredTerminal"/>, so the decisions can be asserted on
    /// a machine that has no terminal at all. With a <paramref name="lookup"/>, each
    /// candidate also carries the absolute path it would be started as
    /// (<see cref="Resolve"/>), which reads the file system and nothing else.
    /// </summary>
    /// <param name="preferredTerminal">The <c>$TERMINAL</c> environment variable (ignored on macOS).</param>
    /// <param name="configuredTerminal">
    /// The "Terminal" preference (<c>AppSettings.PreferredTerminal</c>), tried before
    /// <c>$TERMINAL</c> and before the probe list: on Linux and Windows a program name or a full
    /// path, on macOS an application name.
    /// </param>
    /// <param name="command">A command for the terminal to run (FEAT-17); null opens a shell.</param>
    /// <param name="kubectlPath">The absolute path of the kubectl that runs <paramref name="command"/>.</param>
    public static TerminalLaunchPlan Plan(
        TerminalHostPlatform platform,
        string contextName,
        string kubeconfigPath,
        string stateDirectory,
        string? preferredTerminal = null,
        char? pathSeparator = null,
        TerminalLookup? lookup = null,
        string? configuredTerminal = null,
        TerminalCommand? command = null,
        string? kubectlPath = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(contextName);
        ArgumentException.ThrowIfNullOrEmpty(kubeconfigPath);
        ArgumentException.ThrowIfNullOrEmpty(stateDirectory);
        if (command is not null && string.IsNullOrEmpty(kubectlPath))
        {
            throw new ArgumentException("A command needs the kubectl that runs it.", nameof(kubectlPath));
        }

        var slug = Slug(contextName);
        var overlayPath = Path.Combine(stateDirectory, $"context-{slug}.kubeconfig");

        // Windows separates KUBECONFIG entries with ';' and everything else with ':',
        // which is exactly Path.PathSeparator — but the plan is built for a named
        // platform, not necessarily the running one, so it is passed in for the tests.
        var separator = pathSeparator ?? (platform == TerminalHostPlatform.Windows ? ';' : ':');
        var kubeconfigValue = $"{overlayPath}{separator}{kubeconfigPath}";

        IReadOnlyList<string>? argv = command is null ? null : [kubectlPath!, .. command.Arguments];

        string? scriptPath = null;
        string? scriptContent = null;
        if (platform == TerminalHostPlatform.MacOs)
        {
            if (argv is null)
            {
                scriptPath = Path.Combine(stateDirectory, $"open-{slug}.command");
                scriptContent = LauncherScript(kubeconfigValue);
            }
            else
            {
                // One script per context and command, so two hand-offs started a moment apart
                // never share a file one of them is still waiting for Terminal to read. It
                // removes itself as it starts, so these do not pile up the way the overlays may.
                scriptPath = Path.Combine(stateDirectory, $"run-{Slug(contextName + "\n" + string.Join('\0', argv))}.command");
                scriptContent = CommandScript(kubeconfigValue, argv);
            }
        }

        var candidates = Candidates(platform, preferredTerminal, scriptPath, configuredTerminal, argv);
        if (lookup is not null)
        {
            candidates = [.. candidates.Select(candidate => Resolve(candidate, lookup))];
        }

        return new TerminalLaunchPlan(
            contextName,
            overlayPath,
            ContextOverlay(contextName),
            kubeconfigValue,
            candidates,
            scriptPath,
            scriptContent)
        {
            Command = command,
        };
    }

    /// <summary>
    /// The absolute path <paramref name="candidate"/> is started as, or a candidate whose
    /// <see cref="TerminalCandidate.ResolvedPath"/> is null when there is none (B4-1).
    ///
    /// <para>
    /// <b>Nothing is ever started by a bare name.</b> With <c>UseShellExecute = false</c>, .NET
    /// looks for a name with no directory in it in the app's own folder and then the
    /// <em>current directory</em> before <c>PATH</c> — on Windows (<c>CreateProcess</c> with no
    /// application name) and on Unix (<c>Process.ResolvePath</c>) alike. A <c>cmd.exe</c> or a
    /// <c>pwsh</c> left in a downloads folder or a cloned repository the app was started from
    /// would then run with <c>KUBECONFIG</c> pointed at a cluster. The shells Windows ships
    /// come from the system directory; everything else is searched for on <c>PATH</c> through
    /// <see cref="FindExecutable"/>, which skips any entry that is not fully qualified; a
    /// path (macOS's <c>/usr/bin/open</c>, a <c>$TERMINAL</c> naming one) is used only when it
    /// is fully qualified and exists. The credential-plugin lookup closed the same hole
    /// first (<c>ExecPluginPath</c>).
    /// </para>
    /// </summary>
    public static TerminalCandidate Resolve(TerminalCandidate candidate, TerminalLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(lookup);

        if (candidate.Refusal is not null)
        {
            return candidate with { ResolvedPath = null };
        }

        var path = candidate.Source switch
        {
            TerminalExecutableSource.Absolute =>
                IsFullyQualified(candidate.Executable) && File.Exists(candidate.Executable) ? candidate.Executable : null,

            TerminalExecutableSource.SystemDirectory =>
                lookup.SystemDirectory is { } system && IsFullyQualified(system)
                    ? Existing(Path.Combine([system, .. candidate.Executable.Split('\\', '/')]))
                    : null,

            _ => FindOnPath(candidate.Executable, lookup),
        };

        if (path is not null && candidate.MacApplication is { } application
            && lookup.ApplicationDirectories is { } folders && !IsApplicationInstalled(application, folders))
        {
            path = null;
        }

        return candidate with { ResolvedPath = path };
    }

    /// <summary>
    /// Whether <paramref name="application"/> is an installed bundle: a full path to a
    /// <c>.app</c> that exists, or <c>&lt;name&gt;.app</c> in one of <paramref name="folders"/>,
    /// which are where LaunchServices finds what <c>open -a</c> names.
    /// </summary>
    private static bool IsApplicationInstalled(string application, IReadOnlyList<string> folders)
    {
        if (IsFullyQualified(application))
        {
            return Directory.Exists(application);
        }

        var bundle = application.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? application : application + ".app";
        return folders.Any(folder => IsFullyQualified(folder) && Directory.Exists(Path.Combine(folder, bundle)));
    }

    private static string? FindOnPath(string name, TerminalLookup lookup)
    {
        // A bare name only. Anything with a directory in it would be resolved against the
        // current directory, which is the lookup this method exists to refuse.
        if (name.Contains('/') || name.Contains('\\'))
        {
            return null;
        }

        // On Windows a name that carries its extension ("pwsh.exe") is looked for as
        // exactly that; FindExecutable would otherwise append every PATHEXT entry to it.
        return lookup.Windows && Path.HasExtension(name)
            ? FindExecutable(Path.GetFileNameWithoutExtension(name), lookup.PathValue, Path.GetExtension(name), windows: true)
            : FindExecutable(name, lookup.PathValue, lookup.PathExt, lookup.Windows);
    }

    /// <summary>
    /// What the "nothing could be opened" notice lists. A candidate that resolved to nothing
    /// is never started — not by its bare name either, which is the lookup that reaches the
    /// current directory — and is named as not found.
    /// </summary>
    internal static List<string> TriedLabels(IEnumerable<TerminalCandidate> candidates) =>
        [.. candidates.Select(c =>
            c.Refusal is { } refusal ? $"{c.Label} ({refusal})"
            : c.ResolvedPath is null ? $"{c.Label} (not found)"
            : c.Label)];

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    private static bool IsFullyQualified(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The terminals to try, best first.
    ///
    /// <para>
    /// <b>Windows deliberately does not use <c>wt.exe</c></b>, and that is a narrowing of
    /// the heuristic worth stating. Windows Terminal is a monarch/peasant application: if
    /// a window is already open, <c>wt.exe</c> asks that process to make the tab, and the
    /// shell is then spawned by <em>it</em> — inheriting its environment, not ours. The
    /// tab would open looking correct and be pointed at whatever cluster that process was
    /// started with, which is the one failure this feature must not have. Starting the
    /// shell directly still gets Windows Terminal wherever it is the default terminal
    /// application (the Windows 11 default), because that is a console-host setting, not
    /// a command line — and it gets conhost on Windows 10, which is the documented
    /// fallback anyway.
    /// </para>
    ///
    /// <para>
    /// <b>macOS goes through a launcher script</b> for the same reason: <c>open</c> hands
    /// the request to LaunchServices, which starts (or reuses) Terminal.app with the
    /// session's environment and not ours. The script is the only thing that reliably
    /// carries <c>KUBECONFIG</c> across that boundary, and it is plain text the user can
    /// read.
    /// </para>
    ///
    /// <para>
    /// <b>Linux terminals are started with no arguments</b> for a shell, which every emulator
    /// here treats as "open my default shell", and inherit the environment normally.
    /// </para>
    ///
    /// <para>
    /// <b>The preference comes first, then <c>$TERMINAL</c>, then the probe list</b> (FEAT-19).
    /// The preference is the "Terminal" setting, for an emulator the list does not know. On
    /// macOS, where <c>$TERMINAL</c> names nothing <c>open</c> can use and is ignored, it is an
    /// application name, and the probe tries iTerm2 and Ghostty before Terminal.app. Windows
    /// Terminal named as the preference is refused rather than started, for the reason above.
    /// </para>
    ///
    /// <para>
    /// <b>A command is a fourth argument shape per platform</b> (FEAT-17), never the plain
    /// shape with something bolted on. Windows hands PowerShell the command as a script
    /// (<see cref="PowerShellCommandArguments"/>) and keeps the window open after it; cmd.exe is
    /// not offered, because its quoting rules do not round-trip an argument list. macOS writes
    /// the command into its launcher script. Linux needs the one thing the plain shape
    /// avoided, a per-emulator flag (<see cref="LinuxCommandPrefix"/>), and runs the command
    /// through <c>/bin/sh</c> so the window stays open on a shell afterwards; an emulator that
    /// takes its command as one string is refused rather than given a string built here.
    /// </para>
    /// </summary>
    /// <param name="command">The full command (kubectl's absolute path first), or null for a shell.</param>
    public static IReadOnlyList<TerminalCandidate> Candidates(
        TerminalHostPlatform platform,
        string? preferredTerminal = null,
        string? launcherScriptPath = null,
        string? configuredTerminal = null,
        IReadOnlyList<string>? command = null)
    {
        var candidates = new List<TerminalCandidate>();

        // The preference, then $TERMINAL (not on macOS), each once.
        var preferences = new List<(string Value, string Label)>();
        if (!string.IsNullOrWhiteSpace(configuredTerminal))
        {
            var value = configuredTerminal.Trim();
            preferences.Add((value, $"Preferred terminal ({value})"));
        }

        if (!string.IsNullOrWhiteSpace(preferredTerminal) && platform != TerminalHostPlatform.MacOs
            && !preferences.Exists(p => string.Equals(p.Value, preferredTerminal.Trim(), StringComparison.Ordinal)))
        {
            var value = preferredTerminal.Trim();
            preferences.Add((value, $"$TERMINAL ({value})"));
        }

        switch (platform)
        {
            case TerminalHostPlatform.Windows:
            {
                IReadOnlyList<string> shellArguments = command is null ? ["-NoLogo"] : PowerShellCommandArguments(command);
                foreach (var (terminal, label) in preferences)
                {
                    var candidate = PreferredCandidate(platform, terminal, label, []);
                    if (IsWindowsTerminal(terminal))
                    {
                        candidate = candidate with
                        {
                            Refusal = "Windows Terminal opens its tabs from a process of its own, which would not carry this "
                                      + "cluster's KUBECONFIG; make it the default terminal application instead",
                        };
                    }
                    else if (command is not null)
                    {
                        candidate = IsPowerShell(terminal)
                            ? candidate with { Arguments = shellArguments }
                            : candidate with { Refusal = "only PowerShell is handed a command" };
                    }

                    candidates.Add(candidate);
                }

                // PowerShell 7 has no fixed home (an MSI under Program Files, a Store app
                // alias, a zip anywhere), so it is found on PATH. The two shells Windows
                // ships are taken from System32 by full path and never searched for.
                candidates.Add(new TerminalCandidate("pwsh.exe", shellArguments, "PowerShell 7"));
                candidates.Add(new TerminalCandidate(
                    @"WindowsPowerShell\v1.0\powershell.exe", shellArguments, "Windows PowerShell",
                    TerminalExecutableSource.SystemDirectory));
                if (command is null)
                {
                    candidates.Add(new TerminalCandidate(
                        "cmd.exe", [], "Command Prompt", TerminalExecutableSource.SystemDirectory));
                }

                break;
            }

            case TerminalHostPlatform.MacOs:
            {
                // The script is what carries the environment; without one there is
                // nothing honest to open, so no bare `open -a Terminal` fallback.
                if (launcherScriptPath is not { Length: > 0 })
                {
                    break;
                }

                var applications = new List<(string Application, string Label)>();
                foreach (var (value, label) in preferences)
                {
                    applications.Add((MacApplicationName(value), label));
                }

                foreach (var (application, label) in (ReadOnlySpan<(string, string)>)
                    [("iTerm", "iTerm2"), ("Ghostty", "Ghostty"), ("Terminal", "Terminal")])
                {
                    if (!applications.Exists(a => string.Equals(a.Application, application, StringComparison.OrdinalIgnoreCase)))
                    {
                        applications.Add((application, label));
                    }
                }

                foreach (var (application, label) in applications)
                {
                    // Ghostty does not run a script it is asked to open; it runs one given to
                    // -e, through a new instance (-n), because the arguments after `--args`
                    // reach only an application `open` starts.
                    IReadOnlyList<string> arguments = string.Equals(application, "Ghostty", StringComparison.OrdinalIgnoreCase)
                        ? ["-na", application, "--args", "-e", launcherScriptPath]
                        : ["-a", application, launcherScriptPath];

                    candidates.Add(new TerminalCandidate("/usr/bin/open", arguments, label, TerminalExecutableSource.Absolute)
                    {
                        MacApplication = application,
                    });
                }

                break;
            }

            default:
            {
                IReadOnlyList<string>? wrapped = command is null ? null : ["/bin/sh", "-c", HoldScript, .. command];

                foreach (var (terminal, label) in preferences)
                {
                    // An emulator the table does not know gets xterm's -e, the convention
                    // Debian's x-terminal-emulator policy also requires; one known to take its
                    // command as one string is refused here too, as it is on the probe list.
                    if (wrapped is not null && TakesOneCommandString(terminal))
                    {
                        candidates.Add(PreferredCandidate(platform, terminal, label, []) with { Refusal = OneStringRefusal });
                        continue;
                    }

                    IReadOnlyList<string> arguments = wrapped is null ? [] : [.. LinuxCommandPrefix(terminal) ?? ["-e"], .. wrapped];
                    candidates.Add(PreferredCandidate(platform, terminal, label, arguments));
                }

                // The freedesktop proposal first (it honours the user's chosen default),
                // then Debian's alternatives symlink, then the emulators themselves.
                foreach (var name in (string[])
                    [
                        "xdg-terminal-exec", "x-terminal-emulator",
                        "ptyxis", "gnome-terminal", "konsole", "xfce4-terminal", "kitty",
                        "alacritty", "wezterm", "foot", "tilix", "terminator",
                        "mate-terminal", "lxterminal", "xterm",
                    ])
                {
                    if (wrapped is null)
                    {
                        candidates.Add(new TerminalCandidate(name, [], name));
                    }
                    else if (LinuxCommandPrefix(name) is { } prefix)
                    {
                        candidates.Add(new TerminalCandidate(name, [.. prefix, .. wrapped], name));
                    }
                    else
                    {
                        candidates.Add(new TerminalCandidate(name, [], name) { Refusal = OneStringRefusal });
                    }
                }

                break;
            }
        }

        return candidates;
    }

    /// <summary>
    /// The preference or <c>$TERMINAL</c> as a candidate. A value with a directory in it is a
    /// path and is used only if it is a full one (Resolve refuses "./term" and "bin/term", which
    /// name the current directory); a bare name is searched for on PATH like every other candidate.
    /// </summary>
    private static TerminalCandidate PreferredCandidate(
        TerminalHostPlatform platform, string terminal, string label, IReadOnlyList<string> arguments)
    {
        var isPath = terminal.Contains('/') || (platform == TerminalHostPlatform.Windows && terminal.Contains('\\'));
        return new TerminalCandidate(
            terminal, arguments, label, isPath ? TerminalExecutableSource.Absolute : TerminalExecutableSource.Path);
    }

    /// <summary>A program's name without its folder and without <c>.exe</c>, for the tables below.</summary>
    private static string ProgramName(string terminal)
    {
        var name = terminal.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private const string OneStringRefusal = "takes its command as one string, which kubeNimbus does not build";

    /// <summary>Emulators whose <c>-e</c> takes the whole command as one string, so an argument list cannot be handed to them.</summary>
    private static bool TakesOneCommandString(string terminal) => ProgramName(terminal) is "tilix" or "lxterminal";

    private static bool IsWindowsTerminal(string terminal) =>
        ProgramName(terminal).ToLowerInvariant() is "wt" or "windowsterminal";

    private static bool IsPowerShell(string terminal) =>
        ProgramName(terminal).ToLowerInvariant() is "pwsh" or "powershell";

    /// <summary>
    /// The application <c>open -a</c> is asked for. iTerm2's bundle is <c>iTerm.app</c>, so
    /// "iTerm2" as typed into the preference is that; anything else is used as written, which
    /// includes a full path to a bundle.
    /// </summary>
    private static string MacApplicationName(string value) => value.Trim().ToLowerInvariant() switch
    {
        "iterm" or "iterm2" or "iterm.app" or "iterm2.app" => "iTerm",
        "ghostty" or "ghostty.app" => "Ghostty",
        "terminal" or "terminal.app" => "Terminal",
        _ => value.Trim(),
    };

    /// <summary>
    /// What a Linux emulator needs in front of a command and its arguments, or null for one
    /// whose flag takes the command as a single string (<c>tilix -e</c>, <c>lxterminal -e</c>),
    /// which would mean building a command line here. Each entry is the emulator's documented
    /// form; none was run here (no Linux desktop), which is why the table is a narrow one.
    /// </summary>
    public static IReadOnlyList<string>? LinuxCommandPrefix(string terminal) => ProgramName(terminal) switch
    {
        // xdg-terminal-exec's whole interface: the command is the arguments.
        "xdg-terminal-exec" or "kitty" or "foot" => [],
        "x-terminal-emulator" or "konsole" or "alacritty" or "xterm" => ["-e"],
        "gnome-terminal" or "ptyxis" => ["--"],
        "xfce4-terminal" or "terminator" or "mate-terminal" => ["-x"],
        "wezterm" => ["start", "--"],
        _ => null,
    };

    /// <summary>
    /// What <c>/bin/sh -c</c> runs on Linux for a command: the command, from its own positional
    /// arguments (so nothing in it is ever parsed by a shell), then the user's shell, so the
    /// window stays open on whatever kubectl printed last and on a prompt with
    /// <c>KUBECONFIG</c> still set.
    /// </summary>
    public const string HoldScript = "\"$0\" \"$@\"; exec \"${SHELL:-/bin/sh}\"";

    /// <summary>
    /// The PowerShell arguments that run <paramref name="command"/> and then stay open
    /// (<c>-NoExit</c>), so a refusal kubectl prints is still on screen and the window is a
    /// prompt on the cluster afterwards. The script goes as <c>-EncodedCommand</c> (UTF-16LE,
    /// base64), which leaves the outer command line nothing but letters, digits and
    /// <c>+/=</c> for .NET's argument quoting and PowerShell's own parsing to agree on.
    /// </summary>
    public static IReadOnlyList<string> PowerShellCommandArguments(IReadOnlyList<string> command) =>
        ["-NoLogo", "-NoExit", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(PowerShellScript(command)))];

    /// <summary>
    /// <c>&amp; 'kubectl.exe' 'exec' …</c>: every argument a single-quoted literal, in which
    /// PowerShell expands nothing. A quote inside one is doubled, and that includes the four
    /// typographic single quotes PowerShell also reads as one (a path like
    /// <c>C:\Users\O’Brien</c> would otherwise end the literal early).
    /// </summary>
    public static string PowerShellScript(IReadOnlyList<string> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return "& " + string.Join(' ', command.Select(PowerShellQuoted));
    }

    private static string PowerShellQuoted(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var c in value)
        {
            builder.Append(c);
            if (c is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B')
            {
                builder.Append(c);
            }
        }

        return builder.Append('\'').ToString();
    }

    /// <summary>
    /// The macOS script for a command: <c>KUBECONFIG</c> as in <see cref="LauncherScript"/>,
    /// the command with every word single-quoted, then a login shell so the window stays open.
    /// It deletes itself as it starts — it is read once — so one is not left behind per
    /// command; the overlay it names is kept, like every overlay (ENG-15).
    /// </summary>
    public static string CommandScript(string kubeconfigValue, IReadOnlyList<string> command) =>
        $"""
        #!/bin/sh
        # Written by kubeNimbus for one command, and removed as it starts.
        # Paths only — there is no credential in this file.
        rm -f "$0"
        KUBECONFIG='{ShellQuoted(kubeconfigValue)}'
        export KUBECONFIG
        {string.Join(' ', command.Select(word => $"'{ShellQuoted(word)}'"))}
        exec "$SHELL" -l

        """;

    /// <summary>
    /// The overlay kubeconfig: the context name and nothing else. Not a copy of anything
    /// — there is no cluster block, no user block and therefore no token, certificate or
    /// exec-plugin invocation in it (hard rule 4).
    /// </summary>
    public static string ContextOverlay(string contextName) =>
        $"""
        # Written by kubeNimbus so an external terminal starts on the right cluster.
        # It holds a context NAME and nothing else — no clusters, no users, no
        # credentials. Your own kubeconfig is merged in after this file through
        # $KUBECONFIG and is never modified; kubectl takes current-context from the
        # first file in the chain that sets one.
        apiVersion: v1
        kind: Config
        current-context: "{YamlQuoted(contextName)}"

        """;

    /// <summary>
    /// The macOS launcher script. <c>exec "$SHELL" -l</c> so the window is an ordinary
    /// login shell rather than a script that has finished — with the caveat, stated here
    /// because it is invisible otherwise, that a profile which exports
    /// <c>KUBECONFIG</c> itself will win over this.
    /// </summary>
    public static string LauncherScript(string kubeconfigValue) =>
        $"""
        #!/bin/sh
        # Written by kubeNimbus. Paths only — there is no credential in this file.
        KUBECONFIG='{ShellQuoted(kubeconfigValue)}'
        export KUBECONFIG
        exec "$SHELL" -l

        """;

    /// <summary>
    /// Where <c>kubectl</c> is, or null. Searched on this process's <c>PATH</c> plus the
    /// handful of directories a login shell adds that a GUI process does not see.
    ///
    /// <para>
    /// Null here is <b>weak evidence</b>, and that is why a miss warns rather than
    /// blocks. A GUI launched from Explorer, the Dock or the Microsoft Store inherits a
    /// minimal environment — the same reason <c>$KUBECONFIG</c> does not reach it — so
    /// kubectl can easily be missing from our PATH and present in the terminal's. The
    /// terminal is also worth opening without kubectl at all: <c>KUBECONFIG</c> is what
    /// helm, k9s, stern and kubectx read too.
    /// </para>
    /// </summary>
    public static string? FindKubectl() => FindExecutable(
        "kubectl",
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetEnvironmentVariable("PATHEXT"),
        CurrentPlatform == TerminalHostPlatform.Windows,
        LoginShellDirectories(CurrentPlatform));

    /// <summary>
    /// Directories a login shell routinely puts on PATH that a GUI process does not
    /// inherit. Homebrew's two prefixes are the ones that matter in practice.
    /// </summary>
    public static IReadOnlyList<string> LoginShellDirectories(TerminalHostPlatform platform)
    {
        if (platform == TerminalHostPlatform.Windows)
        {
            return [];
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            "/usr/local/bin",
            "/opt/homebrew/bin",
            "/opt/local/bin",
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, "bin"),
        ];
    }

    /// <summary>
    /// Resolves an executable the way the OS would: each PATH entry in order, then the
    /// extra directories, trying every <c>PATHEXT</c> extension on Windows. Pure — the
    /// PATH is passed in — so the Windows rules are testable from Linux and vice versa.
    /// </summary>
    public static string? FindExecutable(
        string name,
        string? pathValue,
        string? pathExt,
        bool windows,
        IReadOnlyList<string>? extraDirectories = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var separator = windows ? ';' : ':';
        var extensions = windows
            ? (pathExt ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [""];

        var directories = (pathValue ?? "")
            .Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(extraDirectories ?? []);

        foreach (var directory in directories)
        {
            // A relative entry (".", "bin", or an empty one between two separators) names
            // the current directory, and whoever can put a file there would choose what runs
            // as kubectl or as a credential plugin. Go's exec.LookPath refuses these with
            // exec.ErrDot; so does this. Fully qualified by the host's own rules, since the
            // probe below is a File.Exists on this host.
            bool qualified;
            try
            {
                qualified = Path.IsPathFullyQualified(directory);
            }
            catch (ArgumentException)
            {
                qualified = false;
            }

            if (!qualified)
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory, name + extension);
                }
                catch (ArgumentException)
                {
                    // A PATH entry with invalid path characters in it. Real, and not
                    // worth failing the whole probe over.
                    break;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Writes the overlay and opens a terminal on <paramref name="context"/>.
    ///
    /// <para>
    /// Never throws for an ordinary failure: nothing on the candidate list starting, or
    /// the state directory being unwritable, comes back as an outcome the UI states
    /// (UI rule 9) rather than as an exception on a fire-and-forget command.
    /// </para>
    /// </summary>
    public static Task<TerminalLaunchResult> OpenAsync(
        ClusterContext context, CancellationToken cancellationToken = default) =>
        OpenAsync(context, command: null, cancellationToken);

    /// <summary>
    /// Writes the overlay and opens a terminal on <paramref name="context"/> that runs
    /// <paramref name="command"/> (FEAT-17: an exec session; FEAT-27: a node shell), or a
    /// plain shell when it is null.
    ///
    /// <para>
    /// <b>A missing kubectl blocks a command</b>, where it only warns for a plain terminal: the
    /// command <em>is</em> kubectl, and a window that opens to "kubectl: command not found" is
    /// the failure stated in the wrong place. Checked before anything is written.
    /// </para>
    ///
    /// <para>
    /// The "Terminal" preference is read here, at the press, from the same
    /// <c>settings.json</c> the app writes — like the delete confirm, so a change applies to
    /// the very next launch.
    /// </para>
    /// </summary>
    public static Task<TerminalLaunchResult> OpenAsync(
        ClusterContext context, TerminalCommand? command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The demo cluster's objects ship inside the binary; there is no kubeconfig
        // behind it and so nothing for a terminal to be pointed at. Refused in place
        // with a reason, never a silent no-op (the demo section's rule 5).
        if (context.IsDemo)
        {
            return Task.FromResult(new TerminalLaunchResult(
                TerminalLaunchOutcome.NoKubeconfig, null, null, "", context.Name, [], null) { Command = command?.Summary });
        }

        // Process.Start blocks — `open` on macOS notably so — and this is called from a
        // command on the UI thread.
        return Task.Run(() => Open(context, command, cancellationToken), cancellationToken);
    }

    /// <summary>Test seam: what <see cref="FindKubectl"/> answers, so a test controls "no kubectl".</summary>
    internal static readonly AsyncLocal<Func<string?>?> KubectlProbeOverride = new();

    /// <summary>Test seam: the lookup the launch resolves against, so a test can make nothing resolve.</summary>
    internal static readonly AsyncLocal<TerminalLookup?> LookupOverride = new();

    /// <summary>The "Terminal" preference, read from the file the app writes. Null when unset or unreadable.</summary>
    private static string? ConfiguredTerminal() => new KubeNimbus.Core.Settings.AppSettingsStore().Load().PreferredTerminal;

    private static TerminalLaunchResult Open(ClusterContext context, TerminalCommand? command, CancellationToken cancellationToken)
    {
        var kubectl = KubectlProbeOverride.Value is { } probe ? probe() : FindKubectl();
        if (command is not null && kubectl is null)
        {
            return new TerminalLaunchResult(TerminalLaunchOutcome.NoKubectl, null, null, "", context.Name, [], null)
            {
                Command = command.Summary,
            };
        }

        var plan = Plan(
            CurrentPlatform,
            context.Name,
            context.KubeconfigPath,
            StateDirectory,
            Environment.GetEnvironmentVariable("TERMINAL"),
            lookup: LookupOverride.Value ?? TerminalLookup.Current,
            configuredTerminal: ConfiguredTerminal(),
            command: command,
            kubectlPath: kubectl);

        var tried = TriedLabels(plan.Candidates);

        try
        {
            // Atomically: a terminal already open on this context names the overlay in its
            // KUBECONFIG, and a half-written file read by its next kubectl would drop the
            // pinned context — and with it, the guarantee of which cluster it talks to.
            AppDataDirectory.WriteAllTextAtomically(plan.OverlayPath, plan.OverlayContent);

            if (plan.LauncherScriptPath is { } script && plan.LauncherScriptContent is { } body)
            {
                AppDataDirectory.WriteAllTextAtomically(
                    script, body, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TerminalLaunchResult(
                TerminalLaunchOutcome.Failed, null, kubectl, plan.KubeconfigValue, context.Name, tried,
                ex.Message)
            {
                Command = command?.Summary,
            };
        }

        string? lastError = null;

        foreach (var candidate in plan.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (candidate.ResolvedPath is not { } executable)
            {
                continue;
            }

            var info = new ProcessStartInfo(executable)
            {
                // Required for Environment to be honoured at all, and it is the whole
                // mechanism here.
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };

            foreach (var argument in candidate.Arguments)
            {
                info.ArgumentList.Add(argument);
            }

            // Paths only. KUBECONFIG names two files; KUBENIMBUS_CONTEXT is a courtesy
            // for a prompt (kube-ps1 and friends) and is not read by anything here.
            info.Environment["KUBECONFIG"] = plan.KubeconfigValue;
            info.Environment["KUBENIMBUS_CONTEXT"] = context.Name;

            try
            {
                using var process = Process.Start(info);
                if (process is null)
                {
                    continue;
                }

                // An emulator with no display exits immediately and non-zero; reporting
                // "opened" over that would be the one lie this result must not tell.
                if (process.WaitForExit(StartupGrace) && process.ExitCode != 0)
                {
                    lastError = $"{candidate.Label} exited immediately (code {process.ExitCode}).";
                    continue;
                }

                return new TerminalLaunchResult(
                    TerminalLaunchOutcome.Opened, candidate.Label, kubectl, plan.KubeconfigValue, context.Name,
                    tried, null)
                {
                    Command = command?.Summary,
                };
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                           or PlatformNotSupportedException)
            {
                // Not installed, not executable, or not startable from here. Next.
                lastError = ex.Message;
            }
        }

        return new TerminalLaunchResult(
            TerminalLaunchOutcome.NoTerminal, null, kubectl, plan.KubeconfigValue, context.Name, tried, lastError)
        {
            Command = command?.Summary,
        };
    }

    /// <summary>
    /// A short, stable, filesystem-safe name for a context. Hashed rather than sanitized
    /// because real context names are ARNs and URLs — <c>arn:aws:eks:…:cluster/x</c> —
    /// and any sanitizer that made those into filenames would map two different clusters
    /// onto one file.
    /// </summary>
    private static string Slug(string contextName) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contextName)))[..12].ToLowerInvariant();

    /// <summary>Escapes a YAML double-quoted scalar. Context names contain colons routinely.</summary>
    private static string YamlQuoted(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>Escapes a POSIX single-quoted string, the only quoting that needs no other rules.</summary>
    private static string ShellQuoted(string value) =>
        value.Replace("'", "'\\''", StringComparison.Ordinal);
}
