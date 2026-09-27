using k8s.KubeConfigModels;

namespace KubeNimbus.Core;

/// <summary>
/// Finds a kubeconfig's exec credential plugin the way a login shell would, and rewrites
/// the command to the path it found — in memory, on the object the client configuration
/// is built from, and nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The client starts the plugin with <c>FileName = exec.command</c> verbatim
/// and <c>UseShellExecute = false</c>, so a bare <c>command: aws</c> is looked up on
/// <em>this process's</em> <c>PATH</c>. A GUI launched from Finder, the Dock, a desktop
/// launcher or the Microsoft Store does not get the <c>PATH</c> a login shell builds, so
/// <c>aws</c> in <c>/opt/homebrew/bin</c> — present in every terminal on the same machine —
/// "does not exist". It is the most reported connection failure in every Kubernetes GUI's
/// tracker (see <c>docs/research/2026-08-18-connecting-to-a-cluster.md</c> §1.1), and the
/// standard workaround is to hand-edit the kubeconfig to an absolute path.
/// </para>
/// <para>
/// <b>Rewriting the command rather than this process's <c>PATH</c>.</b> Mutating the
/// process environment would change what every later child process sees (the exec pane's
/// shell, the terminal launcher's probe), for the sake of one kubeconfig entry. The
/// rewrite is local to the configuration being built, and it is rebuilt from the file on
/// every connect, so nothing is cached and nothing is persisted (hard rule 4).
/// </para>
/// <para>
/// <b>The plugin's own <c>PATH</c> is extended too</b>, by appending the same directories
/// when they exist and are missing — unless the kubeconfig sets <c>PATH</c> for the plugin
/// itself, which is an explicit answer. <c>gke-gcloud-auth-plugin</c> runs <c>gcloud</c>
/// and kubelogin's Azure CLI mode runs <c>az</c>; finding the plugin only to have it fail
/// to find its own tool would move the bug one level down. Appended, not prepended, so
/// nothing the system already resolves is shadowed.
/// </para>
/// <para>
/// A command containing a path separator but not rooted is resolved against the
/// kubeconfig's own directory, which is what kubectl (client-go) does with it.
/// </para>
/// </remarks>
internal static class ExecPluginPath
{
    private static readonly AsyncLocal<IReadOnlyList<string>?> DirectoriesOverride = new();

    /// <summary>
    /// Replaces the login-shell directories for the calling flow — tests only. An
    /// <see cref="AsyncLocal{T}"/> rather than a static, because tests run in parallel and
    /// the value has to reach the thread-pool continuation that builds the configuration.
    /// </summary>
    internal static IDisposable OverrideDirectories(IReadOnlyList<string> directories)
    {
        var previous = DirectoriesOverride.Value;
        DirectoriesOverride.Value = directories;
        return new Restore(() => DirectoriesOverride.Value = previous);
    }

    /// <summary>The directories searched after <c>PATH</c>: <see cref="TerminalLauncher.LoginShellDirectories"/>.</summary>
    internal static IReadOnlyList<string> ExtraDirectories =>
        DirectoriesOverride.Value ?? TerminalLauncher.LoginShellDirectories(TerminalLauncher.CurrentPlatform);

    /// <summary>
    /// Applies the resolution to <paramref name="exec"/> in place, using this process's
    /// environment. Returns what it did, for the connection report.
    /// </summary>
    internal static ExecPluginResolution Apply(ExternalExecution exec, string? kubeconfigDirectory)
    {
        ArgumentNullException.ThrowIfNull(exec);

        var windows = OperatingSystem.IsWindows();
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        var extra = ExtraDirectories;
        var command = exec.Command ?? "";
        var resolved = Resolve(command, kubeconfigDirectory, pathValue, Environment.GetEnvironmentVariable("PATHEXT"), windows, extra);
        if (resolved is not null)
        {
            exec.Command = resolved;
        }

        var added = MissingDirectories(pathValue, windows, extra);
        if (added.Count > 0 && !SetsPath(exec))
        {
            var separator = windows ? ";" : ":";
            var value = string.IsNullOrEmpty(pathValue)
                ? string.Join(separator, added)
                : pathValue.TrimEnd(separator[0]) + separator + string.Join(separator, added);
            exec.EnvironmentVariables ??= [];
            exec.EnvironmentVariables.Add(new Dictionary<string, string> { ["name"] = "PATH", ["value"] = value });
        }
        else
        {
            added = [];
        }

        return new ExecPluginResolution(command, resolved, added);
    }

    /// <summary>
    /// The absolute path <paramref name="command"/> should be run as, or null to leave it
    /// alone — already absolute, empty, or not found anywhere (in which case the client's
    /// own "could not start" error, which names the command, is the right message).
    /// Pure: every input is passed in, so the Windows rules are testable anywhere.
    /// </summary>
    internal static string? Resolve(
        string command,
        string? kubeconfigDirectory,
        string? pathValue,
        string? pathExt,
        bool windows,
        IReadOnlyList<string> extraDirectories)
    {
        if (string.IsNullOrWhiteSpace(command) || Path.IsPathFullyQualified(command))
        {
            return null;
        }

        var hasSeparator = command.Contains('/') || (windows && command.Contains('\\'));
        if (hasSeparator)
        {
            return kubeconfigDirectory is { Length: > 0 }
                ? Path.GetFullPath(Path.Combine(kubeconfigDirectory, command))
                : null;
        }

        // On Windows a command that already carries an extension ("kubelogin.exe") is
        // looked up as exactly that name; FindExecutable would otherwise append every
        // PATHEXT extension to it and match nothing.
        if (windows && Path.HasExtension(command))
        {
            return TerminalLauncher.FindExecutable(
                Path.GetFileNameWithoutExtension(command), pathValue, Path.GetExtension(command), windows, extraDirectories);
        }

        return TerminalLauncher.FindExecutable(command, pathValue, pathExt, windows, extraDirectories);
    }

    /// <summary>The extra directories that exist and are not already on <paramref name="pathValue"/>.</summary>
    internal static IReadOnlyList<string> MissingDirectories(string? pathValue, bool windows, IReadOnlyList<string> extraDirectories)
    {
        var comparison = windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var present = new HashSet<string>(
            (pathValue ?? "").Split(windows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.TrimEnd('/', '\\')),
            comparison);

        return [.. extraDirectories
            .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d) && present.Add(d.TrimEnd('/', '\\')))];
    }

    private static bool SetsPath(ExternalExecution exec) =>
        exec.EnvironmentVariables?.Any(v =>
            v.TryGetValue("name", out var name)
            && string.Equals(name, "PATH", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        ?? false;

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

/// <summary>What <see cref="ExecPluginPath.Apply"/> did to one exec entry.</summary>
/// <param name="Command">The command as the kubeconfig wrote it.</param>
/// <param name="ResolvedPath">The path it will be run as, or null when it was left alone.</param>
/// <param name="AddedToPath">Directories appended to the plugin's own <c>PATH</c>.</param>
internal sealed record ExecPluginResolution(string Command, string? ResolvedPath, IReadOnlyList<string> AddedToPath);
