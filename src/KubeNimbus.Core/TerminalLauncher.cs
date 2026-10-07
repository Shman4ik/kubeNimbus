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
}

/// <summary>
/// What <see cref="TerminalLauncher.Resolve"/> searches: this process's <c>PATH</c> and
/// <c>PATHEXT</c> and the Windows system directory, passed in so the rules are testable with a
/// fake PATH and a fake <c>System32</c>.
/// </summary>
public sealed record TerminalLookup(string? PathValue, string? PathExt, string? SystemDirectory, bool Windows)
{
    /// <summary>The running process's own values.</summary>
    public static TerminalLookup Current => new(
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetEnvironmentVariable("PATHEXT"),
        OperatingSystem.IsWindows() ? Environment.SystemDirectory : null,
        OperatingSystem.IsWindows());
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
    string? LauncherScriptContent);

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
    public static TerminalLaunchPlan Plan(
        TerminalHostPlatform platform,
        string contextName,
        string kubeconfigPath,
        string stateDirectory,
        string? preferredTerminal = null,
        char? pathSeparator = null,
        TerminalLookup? lookup = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(contextName);
        ArgumentException.ThrowIfNullOrEmpty(kubeconfigPath);
        ArgumentException.ThrowIfNullOrEmpty(stateDirectory);

        var slug = Slug(contextName);
        var overlayPath = Path.Combine(stateDirectory, $"context-{slug}.kubeconfig");

        // Windows separates KUBECONFIG entries with ';' and everything else with ':',
        // which is exactly Path.PathSeparator — but the plan is built for a named
        // platform, not necessarily the running one, so it is passed in for the tests.
        var separator = pathSeparator ?? (platform == TerminalHostPlatform.Windows ? ';' : ':');
        var kubeconfigValue = $"{overlayPath}{separator}{kubeconfigPath}";

        string? scriptPath = null;
        string? scriptContent = null;
        if (platform == TerminalHostPlatform.MacOs)
        {
            scriptPath = Path.Combine(stateDirectory, $"open-{slug}.command");
            scriptContent = LauncherScript(kubeconfigValue);
        }

        var candidates = Candidates(platform, preferredTerminal, scriptPath);
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
            scriptContent);
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

        return candidate with { ResolvedPath = path };
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
        [.. candidates.Select(c => c.ResolvedPath is null ? $"{c.Label} (not found)" : c.Label)];

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
    /// <b>Linux terminals are started with no arguments</b>, which every emulator here
    /// treats as "open my default shell", and inherit the environment normally.
    /// <c>$TERMINAL</c> is honoured first because a user who set it has already answered
    /// this question.
    /// </para>
    /// </summary>
    public static IReadOnlyList<TerminalCandidate> Candidates(
        TerminalHostPlatform platform, string? preferredTerminal = null, string? launcherScriptPath = null)
    {
        var candidates = new List<TerminalCandidate>();

        if (!string.IsNullOrWhiteSpace(preferredTerminal) && platform != TerminalHostPlatform.MacOs)
        {
            // A value with a directory in it is a path and is used only if it is a full one
            // (Resolve refuses "./term" and "bin/term", which name the current directory);
            // a bare name is searched for on PATH like every other candidate.
            var terminal = preferredTerminal.Trim();
            var isPath = terminal.Contains('/') || (platform == TerminalHostPlatform.Windows && terminal.Contains('\\'));
            candidates.Add(new TerminalCandidate(
                terminal, [], $"$TERMINAL ({terminal})",
                isPath ? TerminalExecutableSource.Absolute : TerminalExecutableSource.Path));
        }

        switch (platform)
        {
            case TerminalHostPlatform.Windows:
                // PowerShell 7 has no fixed home (an MSI under Program Files, a Store app
                // alias, a zip anywhere), so it is found on PATH. The two shells Windows
                // ships are taken from System32 by full path and never searched for.
                candidates.Add(new TerminalCandidate("pwsh.exe", ["-NoLogo"], "PowerShell 7"));
                candidates.Add(new TerminalCandidate(
                    @"WindowsPowerShell\v1.0\powershell.exe", ["-NoLogo"], "Windows PowerShell",
                    TerminalExecutableSource.SystemDirectory));
                candidates.Add(new TerminalCandidate(
                    "cmd.exe", [], "Command Prompt", TerminalExecutableSource.SystemDirectory));
                break;

            case TerminalHostPlatform.MacOs:
                // The script is what carries the environment; without one there is
                // nothing honest to open, so no bare `open -a Terminal` fallback.
                if (launcherScriptPath is { Length: > 0 })
                {
                    candidates.Add(new TerminalCandidate(
                        "/usr/bin/open", ["-a", "Terminal", launcherScriptPath], "Terminal",
                        TerminalExecutableSource.Absolute));
                }

                break;

            default:
                // The freedesktop proposal first (it honours the user's chosen default),
                // then Debian's alternatives symlink, then the emulators themselves.
                candidates.Add(new TerminalCandidate("xdg-terminal-exec", [], "xdg-terminal-exec"));
                candidates.Add(new TerminalCandidate("x-terminal-emulator", [], "x-terminal-emulator"));
                foreach (var name in (string[])
                    [
                        "ptyxis", "gnome-terminal", "konsole", "xfce4-terminal", "kitty",
                        "alacritty", "wezterm", "foot", "tilix", "terminator",
                        "mate-terminal", "lxterminal", "xterm",
                    ])
                {
                    candidates.Add(new TerminalCandidate(name, [], name));
                }

                break;
        }

        return candidates;
    }

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
        ClusterContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The demo cluster's objects ship inside the binary; there is no kubeconfig
        // behind it and so nothing for a terminal to be pointed at. Refused in place
        // with a reason, never a silent no-op (the demo section's rule 5).
        if (context.IsDemo)
        {
            return Task.FromResult(new TerminalLaunchResult(
                TerminalLaunchOutcome.NoKubeconfig, null, null, "", context.Name, [], null));
        }

        // Process.Start blocks — `open` on macOS notably so — and this is called from a
        // command on the UI thread.
        return Task.Run(() => Open(context, cancellationToken), cancellationToken);
    }

    private static TerminalLaunchResult Open(ClusterContext context, CancellationToken cancellationToken)
    {
        var plan = Plan(
            CurrentPlatform,
            context.Name,
            context.KubeconfigPath,
            StateDirectory,
            Environment.GetEnvironmentVariable("TERMINAL"),
            lookup: TerminalLookup.Current);

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
                TerminalLaunchOutcome.Failed, null, FindKubectl(), plan.KubeconfigValue, context.Name, tried,
                ex.Message);
        }

        var kubectl = FindKubectl();
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
                    tried, null);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                           or PlatformNotSupportedException)
            {
                // Not installed, not executable, or not startable from here. Next.
                lastError = ex.Message;
            }
        }

        return new TerminalLaunchResult(
            TerminalLaunchOutcome.NoTerminal, null, kubectl, plan.KubeconfigValue, context.Name, tried, lastError);
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
