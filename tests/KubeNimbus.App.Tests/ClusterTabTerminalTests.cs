using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// What the tab says after "open a terminal on this cluster".
///
/// <para>
/// The wording <em>is</em> the deliverable for two of the four outcomes. A terminal
/// opens in front of the app, so kubeNimbus is not what the user is looking at when it
/// works — which means "kubectl is not on this machine" and "nothing here could be
/// opened at all" are only ever seen if the tab still says so afterwards. Both are
/// pinned here; neither needs a terminal, a display or a process.
/// </para>
/// </summary>
public class ClusterTabTerminalTests
{
    private static TerminalLaunchResult Result(
        TerminalLaunchOutcome outcome,
        string? terminal = "xterm",
        string? kubectl = "/usr/bin/kubectl",
        string? error = null) =>
        new(outcome, terminal, kubectl, "/state/context-abc.kubeconfig:/home/dev/.kube/config",
            "payments-prod", ["xdg-terminal-exec", "xterm"], error);

    /// <summary>The success line names the cluster and the value, and is not an alarm.</summary>
    [Test]
    public async Task OpeningSuccessfullyNamesTheClusterAndTheKubeconfigChain()
    {
        var (message, warning, error) = TerminalNoticeViewModel.DescribeTerminalLaunch(
            Result(TerminalLaunchOutcome.Opened));

        await Assert.That(message).Contains("payments-prod");
        await Assert.That(message).Contains("/state/context-abc.kubeconfig:/home/dev/.kube/config");
        await Assert.That(warning).IsFalse();
        await Assert.That(error).IsFalse();
    }

    /// <summary>
    /// #290: someone who set the Terminal preference to <c>wt</c> saw PowerShell open and a
    /// notice that said nothing about the preference. The notice now names the preference and
    /// the reason it was refused, as a warning, on the plain terminal and on a hand-off alike.
    /// </summary>
    [Test]
    public async Task APassedOverPreferenceIsNamedWithItsReason()
    {
        const string refused = "Preferred terminal (wt): Windows Terminal opens its tabs from a process of its own";
        var result = Result(TerminalLaunchOutcome.Opened, terminal: "Windows PowerShell") with { PassedOver = [refused] };

        var (message, warning, error) = TerminalNoticeViewModel.DescribeTerminalLaunch(result);
        await Assert.That(message).StartsWith("Opened Windows PowerShell on payments-prod.");
        await Assert.That(message).Contains(refused);
        await Assert.That(warning).IsTrue();
        await Assert.That(error).IsFalse();

        var (handoff, handoffWarning, _) = TerminalHandoff.Describe(result with { Command = "kubectl exec -it api-0 -- sh" });
        await Assert.That(handoff).Contains(refused);
        await Assert.That(handoffWarning).IsTrue();

        var (plain, _, _) = TerminalNoticeViewModel.DescribeTerminalLaunch(Result(TerminalLaunchOutcome.Opened));
        await Assert.That(plain).DoesNotContain("preferred");
    }

    /// <summary>
    /// #290: the log pane's Range tooltip mentions the demo only on the demo cluster, where the
    /// combo is disabled; on a real cluster it is enabled and the demo has nothing to do with it.
    /// </summary>
    [Test]
    public async Task TheRangeTooltipMentionsTheDemoOnlyOnTheDemo()
    {
        await Assert.That(LogRange.Tooltip(isDemo: true)).Contains("demo");
        await Assert.That(LogRange.Tooltip(isDemo: false)).DoesNotContain("demo");
    }
    /// <summary>
    /// The item's own acceptance criterion: it says so when kubectl is missing. A
    /// warning, not an error — the terminal did open, KUBECONFIG is set, and everything
    /// else that reads a kubeconfig works — and it says why the probe may be wrong,
    /// because a GUI's PATH is routinely shorter than a shell's.
    /// </summary>
    [Test]
    public async Task AMissingKubectlIsStatedAsAWarningAndNotAFailure()
    {
        var (message, warning, error) = TerminalNoticeViewModel.DescribeTerminalLaunch(
            Result(TerminalLaunchOutcome.Opened, kubectl: null));

        await Assert.That(message).Contains("kubectl was not found");
        await Assert.That(message).Contains("PATH");
        await Assert.That(warning).IsTrue();
        await Assert.That(error).IsFalse();
    }

    /// <summary>
    /// Nothing opened: an error that lists what was tried and hands over the exact
    /// KUBECONFIG value, so the gesture is still completable by hand.
    /// </summary>
    [Test]
    public async Task NoTerminalListsWhatWasTriedAndHandsOverTheValue()
    {
        var (message, warning, error) = TerminalNoticeViewModel.DescribeTerminalLaunch(
            Result(TerminalLaunchOutcome.NoTerminal, terminal: null, kubectl: null));

        await Assert.That(message).Contains("xdg-terminal-exec");
        await Assert.That(message).Contains("xterm");
        await Assert.That(message).Contains("/state/context-abc.kubeconfig:/home/dev/.kube/config");
        await Assert.That(error).IsTrue();
        await Assert.That(warning).IsFalse();
    }

    [Test]
    public async Task APreparationFailureCarriesTheUnderlyingReason()
    {
        var (message, _, error) = TerminalNoticeViewModel.DescribeTerminalLaunch(
            Result(TerminalLaunchOutcome.Failed, error: "Permission denied"));

        await Assert.That(message).Contains("Permission denied");
        await Assert.That(error).IsTrue();
    }

    /// <summary>
    /// The demo cluster refuses in place with a reason, never a silent no-op (the demo
    /// section's rule 5) — and the reason names the actual obstacle, which is that there
    /// is no kubeconfig behind it rather than that the feature is unfinished.
    /// </summary>
    [Test]
    public async Task TheDemoClusterRefusesInPlaceAndSaysWhy()
    {
        var (message, warning, error) = TerminalNoticeViewModel.DescribeTerminalLaunch(
            new TerminalLaunchResult(
                TerminalLaunchOutcome.NoKubeconfig, null, null, "", ClusterContext.Demo.Name, [], null));

        await Assert.That(message).Contains("kubeconfig");
        await Assert.That(message).Contains("demo cluster");
        await Assert.That(warning).IsTrue();
        await Assert.That(error).IsFalse();
    }

    /// <summary>
    /// End to end through the real command on a demo tab — the one path that runs whole
    /// without an API server or a terminal. It must leave a stated notice behind, not an
    /// empty strip.
    /// </summary>
    [Test]
    public async Task TheCommandOnADemoTabLeavesAStatedNotice()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);

        await tab.Terminal.OpenInTerminalCommand.ExecuteAsync(null);

        await Assert.That(tab.Terminal.Notice).IsNotNull();
        await Assert.That(tab.Terminal.Notice!).Contains("demo cluster");
        await Assert.That(tab.Terminal.IsWarning).IsTrue();
        await Assert.That(tab.Terminal.IsError).IsFalse();

        tab.Terminal.DismissCommand.Execute(null);
        await Assert.That(tab.Terminal.Notice).IsNull();
    }
}
