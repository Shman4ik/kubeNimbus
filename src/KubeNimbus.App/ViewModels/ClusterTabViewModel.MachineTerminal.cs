using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

// "Open a terminal here": the user's own terminal, handed KUBECONFIG and a pinned context, and
// the notice that says how the launch went (machine-terminal.md).
public sealed partial class ClusterTabViewModel
{
    // ----------------------------------------------------- the machine's terminal
    //
    // "Open a terminal here" — the daily gesture people leave a GUI for, and the one
    // thing this app had no answer to at all. It is not a shell *inside* kubeNimbus
    // (that would need a PTY dependency and would still not be the user's terminal,
    // with their prompt, their fonts and their tools); it is the user's own terminal,
    // handed KUBECONFIG and a pinned current-context. See TerminalLauncher for how the
    // context is pinned without ever writing to the user's kubeconfig, and for why
    // Windows starts a shell directly rather than going through wt.exe.

    /// <summary>
    /// The result of the last "open in terminal", or null when there is nothing to say.
    /// Present only while it has something to report, so it costs no chrome the rest of
    /// the time (UI rule 1), and rendered as an InfoBar rather than a status dot
    /// (UI rule 11). Four outcomes land here — opened, opened-without-kubectl, nothing
    /// to open, and the demo cluster's refusal — because a fire-and-forget command whose
    /// window opens behind the app is exactly the kind that otherwise fails silently.
    /// </summary>
    [ObservableProperty]
    private string? _terminalNotice;

    [ObservableProperty]
    private bool _terminalNoticeIsWarning;

    [ObservableProperty]
    private bool _terminalNoticeIsError;

    /// <summary>
    /// Sentence for each outcome. Static and public so the wording — which is the whole
    /// deliverable of the "says so when kubectl is missing" half of this — can be
    /// asserted without a terminal, a display or a process, and so the screenshot
    /// harness renders the app's own words rather than a fixture's approximation of them.
    /// </summary>
    public static (string Message, bool Warning, bool Error) DescribeTerminalLaunch(TerminalLaunchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Outcome switch
        {
            TerminalLaunchOutcome.NoKubeconfig => (
                "The demo cluster's objects ship inside kubeNimbus — there is no kubeconfig behind it, so there is "
                + "nothing for a terminal to point at. Open a real cluster and try again.",
                true, false),

            TerminalLaunchOutcome.NoTerminal => (
                $"No terminal could be opened. Tried: {string.Join(", ", result.Tried)}. "
                + $"Set KUBECONFIG={result.KubeconfigValue} in a terminal of your own — the context is already "
                + $"pinned to {result.ContextName} in the first file.",
                false, true),

            TerminalLaunchOutcome.Failed => (
                $"Could not prepare the terminal: {result.Error}", false, true),

            _ when result.KubectlMissing => (
                $"Opened {result.TerminalLabel} on {result.ContextName}, but kubectl was not found on this app's "
                + "PATH. KUBECONFIG and the context are set either way, so kubectl, helm, k9s and anything else "
                + "that reads a kubeconfig will use this cluster — a GUI often sees a shorter PATH than your "
                + "shell does, so it may well be there."
                + PassedOverSentence(result),
                true, false),

            _ => (
                $"Opened {result.TerminalLabel} on {result.ContextName}. KUBECONFIG={result.KubeconfigValue} — "
                + "your own kubeconfig is merged in unchanged, the context is pinned in the first file."
                + PassedOverSentence(result),
                result.PassedOver.Count > 0, false),
        };
    }

    /// <summary>
    /// The preferred terminal that was not used, and why, after a fallback opened (#290). The
    /// preferences page says Windows Terminal is refused with its reason; without this sentence
    /// the default PowerShell opened and the notice said nothing about the preference at all.
    /// Empty when the preference opened or none was set.
    /// </summary>
    public static string PassedOverSentence(TerminalLaunchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.PassedOver.Count == 0
            ? ""
            : $" Your preferred terminal was not used — {string.Join("; ", result.PassedOver)}.";
    }

    /// <summary>
    /// Opens the machine's own terminal on this cluster. Deliberately has no
    /// <c>CanExecute</c> gate for the demo cluster: it refuses in place with a reason
    /// (the demo section's rule 5), which is more use than a menu item that is greyed
    /// out for a reason nobody can read.
    /// </summary>
    [RelayCommand]
    private async Task OpenInTerminalAsync()
    {
        TerminalNotice = "Opening a terminal…";
        TerminalNoticeIsWarning = false;
        TerminalNoticeIsError = false;

        TerminalLaunchResult result;
        try
        {
            result = await TerminalLauncher.OpenAsync(Context);
        }
        catch (Exception ex)
        {
            // The launcher answers ordinary failures with an outcome; anything that gets
            // here is unexpected, and still must not take the tab down.
            TerminalNotice = $"Could not open a terminal: {ex.Message}";
            TerminalNoticeIsError = true;
            return;
        }

        var (message, warning, error) = DescribeTerminalLaunch(result);
        TerminalNotice = message;
        TerminalNoticeIsWarning = warning;
        TerminalNoticeIsError = error;
    }

    [RelayCommand]
    private void DismissTerminalNotice() => TerminalNotice = null;
}
