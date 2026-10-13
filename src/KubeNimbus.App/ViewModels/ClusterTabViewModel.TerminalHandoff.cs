using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

// FEAT-17 / FEAT-27 from the resource list (#198): "Exec in my terminal" on a pod row and
// "Node shell in my terminal" on a node row, the same hand-offs the exec pane, node detail and
// the palette make. One path for the row menu and the palette, reported in the tab's own
// terminal notice. Both fire on the click (UI rule 17: handing kubectl to your own terminal
// can be taken back by closing the window), so neither carries an ellipsis.
public sealed partial class ClusterTabViewModel
{
    /// <summary>
    /// A pod row, one row selected (#305), and not a fleet list: a fleet row belongs to another
    /// tab's cluster, and this tab's context would pin the wrong one. The demo cluster is not
    /// gated here; it refuses in place with its reason (the demo section's rule 5).
    /// </summary>
    public bool CanExecInMyTerminal => IsPodRowSelected && !IsFleetView;

    /// <summary>
    /// The row menu shows "Node shell in my terminal" for this row: a node that is not a Windows
    /// node (kubectl debug node/ has no shell to give there), outside a fleet list. With several
    /// rows selected it stays shown, disabled (#305), so the menu does not change shape.
    /// </summary>
    public bool OffersNodeShellOnRow =>
        !IsFleetView
        && SelectedRow is { } row
        && DescriptorFor(row) is { } descriptor
        && NodeActions.IsNodeKind(descriptor)
        && ExecShells.FromNode(row.Resource.Raw) != PodOperatingSystem.Windows;

    /// <inheritdoc cref="OffersNodeShellOnRow"/>
    public bool CanNodeShellInMyTerminal => !HasMultipleSelection && OffersNodeShellOnRow;

    /// <summary><c>kubectl exec -it</c> into the selected pod's default container, in the machine's terminal.</summary>
    [RelayCommand(CanExecute = nameof(CanExecInMyTerminal))]
    private Task ExecInMyTerminalAsync()
    {
        if (ActionRow is not { } pod)
        {
            return Task.CompletedTask;
        }

        return HandOffToTerminalAsync(() => TerminalHandoff.ExecCommand(
            pod.Namespace, pod.Name, PodDetails.DefaultContainer(pod.Resource.Raw) ?? "",
            ExecShells.FromPod(pod.Resource.Raw), shell: null));
    }

    /// <summary><c>kubectl debug node/&lt;name&gt; -it</c> in the machine's terminal (FEAT-27).</summary>
    [RelayCommand(CanExecute = nameof(CanNodeShellInMyTerminal))]
    private Task NodeShellInMyTerminalAsync()
    {
        if (ActionRow is not { } row)
        {
            return Task.CompletedTask;
        }

        var node = row.Name;
        return HandOffToTerminalAsync(() => TerminalHandoff.NodeShellCommand(node), TerminalHandoff.NodeShellLeftover);
    }

    /// <summary>
    /// A hand-off to the machine's terminal, reported in this tab's terminal notice — the
    /// InfoBar "Open a terminal on this cluster" already uses, so neither the row menu nor the
    /// palette adds a surface of its own.
    /// </summary>
    private async Task HandOffToTerminalAsync(Func<TerminalCommand> build, string? openedSuffix = null)
    {
        TerminalNotice = "Opening your terminal…";
        TerminalNoticeIsWarning = false;
        TerminalNoticeIsError = false;

        var result = await TerminalHandoff.OpenAsync(Context, build);
        var (message, warning, error) = TerminalHandoff.Describe(result);
        TerminalNotice = result.Outcome == TerminalLaunchOutcome.Opened && openedSuffix is not null
            ? message + openedSuffix
            : message;
        TerminalNoticeIsWarning = warning;
        TerminalNoticeIsError = error;
    }

    /// <summary>Re-asks both hand-offs; called wherever the other single-row actions are re-asked.</summary>
    private void NotifyTerminalHandoffs()
    {
        OnPropertyChanged(nameof(CanExecInMyTerminal));
        OnPropertyChanged(nameof(OffersNodeShellOnRow));
        OnPropertyChanged(nameof(CanNodeShellInMyTerminal));
        ExecInMyTerminalCommand.NotifyCanExecuteChanged();
        NodeShellInMyTerminalCommand.NotifyCanExecuteChanged();
    }
}
