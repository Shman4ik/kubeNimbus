using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Handing a <c>kubectl</c> command to the machine's own terminal (FEAT-17: "open this exec
/// session in my terminal"; FEAT-27: a node shell), and the sentence for each way it ends.
/// Used by the exec pane, node detail and the command palette, so the three say the same
/// thing about the same outcome. The launch itself is <see cref="TerminalLauncher"/>; see
/// docs/engineering/machine-terminal.md.
/// </summary>
public static class TerminalHandoff
{
    /// <summary>
    /// Builds the command and opens the terminal. A name kubeNimbus will not hand to a shell
    /// (<see cref="TerminalCommand"/>'s check) comes back as a failed outcome with the reason,
    /// never as an exception on a command bound to a button. The demo cluster is refused before
    /// anything is built.
    /// </summary>
    public static async Task<TerminalLaunchResult> OpenAsync(ClusterContext context, Func<TerminalCommand> build)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(build);

        if (context.IsDemo)
        {
            return await TerminalLauncher.OpenAsync(context, command: null);
        }

        TerminalCommand command;
        try
        {
            command = build();
        }
        catch (ArgumentException ex)
        {
            // The sentence without .NET's "(Parameter 'pod')" suffix, which is for a stack trace.
            var reason = ex.ParamName is { } parameter
                ? ex.Message.Replace($" (Parameter '{parameter}')", "", StringComparison.Ordinal)
                : ex.Message;
            return new TerminalLaunchResult(TerminalLaunchOutcome.Failed, null, null, "", context.Name, [], reason);
        }

        try
        {
            return await TerminalLauncher.OpenAsync(context, command);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The launcher answers ordinary failures with an outcome; anything that gets here is
            // unexpected, and still must not take the pane down.
            return new TerminalLaunchResult(TerminalLaunchOutcome.Failed, null, null, "", context.Name, [], ex.Message)
            {
                Command = command.Summary,
            };
        }
    }

    /// <summary>The exec hand-off's command for one container: <see cref="TerminalCommand.ShellFor"/> decides what runs.</summary>
    public static TerminalCommand ExecCommand(
        string @namespace, string pod, string container, PodOperatingSystem os, string? shell) =>
        TerminalCommand.Exec(@namespace, pod, container, TerminalCommand.ShellFor(os, shell));

    /// <summary>
    /// The node shell's command, with the exec pane's debug image: the same pinned BusyBox, so
    /// the one image this app chooses for a user's cluster is chosen in one place
    /// (docs/engineering/exec-terminal.md, "The debug image pin").
    /// </summary>
    public static TerminalCommand NodeShellCommand(string node) =>
        TerminalCommand.NodeShell(node, DebugContainers.DefaultImage);

    /// <summary>
    /// The sentence for each outcome, and whether it is a warning or an error (UI rule 11).
    /// The outcomes a plain terminal shares with a hand-off — the demo cluster, no terminal,
    /// a file that could not be written — use the cluster tab's own words, so a hand-off and
    /// "Open a terminal on this cluster" never describe the same failure two ways.
    /// </summary>
    public static (string Message, bool Warning, bool Error) Describe(TerminalLaunchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // Refused before a plan existed: a name the check would not hand on, or a launch that
        // threw. There is no KUBECONFIG to print, so the tab's "prepare" sentence does not fit.
        if (result.Outcome == TerminalLaunchOutcome.Failed && result.KubeconfigValue.Length == 0)
        {
            return ($"Could not hand this to a terminal: {result.Error}", false, true);
        }

        if (result.Command is not { } command)
        {
            return ClusterTabViewModel.DescribeTerminalLaunch(result);
        }

        return result.Outcome switch
        {
            TerminalLaunchOutcome.NoKubectl => (
                $"kubectl was not found, so nothing was opened: the terminal would run {command}. "
                + (OperatingSystem.IsWindows()
                    ? "Install kubectl or add its folder to PATH, then restart kubeNimbus — a GUI reads PATH when it starts."
                    : "kubeNimbus looked on its PATH and in /usr/local/bin, /opt/homebrew/bin, /opt/local/bin, ~/.local/bin and ~/bin; "
                      + "a GUI often sees a shorter PATH than your shell.")
                + " Open a terminal on this cluster still works without it.",
                false, true),

            TerminalLaunchOutcome.Opened => (
                $"Opened {result.TerminalLabel} on {result.ContextName}, running {command}. "
                + "If kubectl is refused (RBAC, Pod Security), it says so in that window.",
                false, false),

            _ => ClusterTabViewModel.DescribeTerminalLaunch(result),
        };
    }

    /// <summary>
    /// What follows a node shell that opened: kubectl leaves its pod behind when the shell exits,
    /// and the pod has the node's namespaces, so where it went is worth a sentence.
    /// </summary>
    public const string NodeShellLeftover =
        " kubectl debug leaves its node-debugger pod behind, Completed, when the shell exits; delete it when you are done.";
}
