using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The App half of handing a kubectl command to the machine's terminal (FEAT-17, FEAT-27): the
/// sentence for each outcome, what each entry point builds, and the demo cluster's refusal.
/// No test here can start a terminal: every launch goes through the demo cluster (which is
/// refused before anything is built) or a name the check refuses, and the commands themselves
/// are read without a launch.
/// </summary>
public class TerminalHandoffTests
{
    private const string Summary = "kubectl exec -it -n payments api-0 -c app -- /bin/sh";

    private static TerminalLaunchResult Result(TerminalLaunchOutcome outcome, string kubeconfig = "/state/o:/home/k") =>
        new(outcome, outcome == TerminalLaunchOutcome.Opened ? "PowerShell 7" : null, "/usr/local/bin/kubectl", kubeconfig,
            "payments-prod", ["PowerShell 7 (not found)"], outcome == TerminalLaunchOutcome.Failed ? "disk full" : null)
        {
            Command = Summary,
        };

    /// <summary>
    /// A hand-off with no kubectl is an error that opened nothing — unlike a plain terminal,
    /// where a missing kubectl only warns — and the sentence names the command it would have run.
    /// </summary>
    [Test]
    public async Task NoKubectlIsAnErrorThatNamesTheCommand()
    {
        var (message, warning, error) = TerminalHandoff.Describe(Result(TerminalLaunchOutcome.NoKubectl) with { KubectlPath = null });

        await Assert.That(error).IsTrue();
        await Assert.That(warning).IsFalse();
        await Assert.That(message).Contains("kubectl was not found, so nothing was opened");
        await Assert.That(message).Contains(Summary);
    }

    [Test]
    public async Task OpenedNamesTheTerminalTheClusterAndTheCommand()
    {
        var (message, warning, error) = TerminalHandoff.Describe(Result(TerminalLaunchOutcome.Opened));

        await Assert.That(message).Contains("Opened PowerShell 7 on payments-prod, running " + Summary);
        await Assert.That(warning || error).IsFalse();
    }

    /// <summary>The outcomes a hand-off shares with a plain terminal are worded by the tab's own description.</summary>
    [Test]
    public async Task SharedOutcomesUseTheTabsOwnWords()
    {
        var noTerminal = Result(TerminalLaunchOutcome.NoTerminal);

        await Assert.That(TerminalHandoff.Describe(noTerminal)).IsEqualTo(ClusterTabViewModel.DescribeTerminalLaunch(noTerminal));
        await Assert.That(TerminalHandoff.Describe(Result(TerminalLaunchOutcome.Failed)).Message)
            .IsEqualTo("Could not prepare the terminal: disk full");
    }

    /// <summary>
    /// The demo cluster is refused before the command is even built: its objects are invented,
    /// so there is no kubeconfig to pin and nothing a kubectl could reach.
    /// </summary>
    [Test]
    public async Task TheDemoClusterIsRefusedBeforeAnythingIsBuilt()
    {
        var built = false;
        var result = await TerminalHandoff.OpenAsync(ClusterContext.Demo, () =>
        {
            built = true;
            return TerminalCommand.Exec("payments", "api-0", "app", ["/bin/sh"]);
        });

        await Assert.That(built).IsFalse();
        await Assert.That(result.Outcome).IsEqualTo(TerminalLaunchOutcome.NoKubeconfig);
        await Assert.That(TerminalHandoff.Describe(result).Message).Contains("demo cluster");
    }

    /// <summary>A name the check refuses comes back as a stated failure, never an exception on a button's command.</summary>
    [Test]
    public async Task ARefusedNameIsAStatedFailure()
    {
        var result = await TerminalHandoff.OpenAsync(TestObjects.Context, () => TerminalHandoff.NodeShellCommand("worker 1; id"));
        var (message, _, error) = TerminalHandoff.Describe(result);

        await Assert.That(result.Outcome).IsEqualTo(TerminalLaunchOutcome.Failed);
        await Assert.That(error).IsTrue();
        await Assert.That(message).StartsWith("Could not hand this to a terminal: 'worker 1; id' is not a valid node name");
    }

    // ------------------------------------------------------------------ the exec pane

    /// <summary>
    /// The exec pane hands out the container it is in and the shell typed into its box, and
    /// with the box empty the bash-else-sh probe a terminal can run in one go.
    /// </summary>
    [Test]
    public async Task TheExecPaneHandsOutItsContainerAndTheShellInItsBox()
    {
        TestObjects.RedirectStores();
        var pane = new ExecTabViewModel(client: null, "payments", "api-0", "app");

        await Assert.That(pane.BuildHandoffCommand().Summary)
            .IsEqualTo($"kubectl exec -it -n payments api-0 -c app -- /bin/sh -c '{TerminalCommand.ShellProbe}'");

        pane.ShellCommand = "/bin/zsh";
        await Assert.That(pane.BuildHandoffCommand().Summary).IsEqualTo("kubectl exec -it -n payments api-0 -c app -- /bin/zsh");
    }

    // ----------------------------------------------------------------- node detail

    /// <summary>
    /// Node detail's node shell on the demo cluster refuses in place with the demo sentence,
    /// in the pane's own InfoBar, and the notice dismisses.
    /// </summary>
    [Test]
    public async Task TheNodeShellOnTheDemoClusterRefusesInPlace()
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.SelectKindCommand.Execute(tab.SidebarSections.SelectMany(s => s.Kinds).First(k => k.Descriptor is { Group: "", Kind: "Node" }));
        tab.SelectedRow = tab.Rows.First();
        tab.OpenSelectedCommand.Execute(null);
        var detail = (NodeDetailTabViewModel)tab.SelectedInspectorTab!;

        await Assert.That(detail.CanOfferNodeShell).IsTrue();
        await detail.OpenNodeShellCommand.ExecuteAsync(null);

        await Assert.That(detail.NodeShellNotice).IsNotNull();
        await Assert.That(detail.NodeShellNotice!).Contains("demo cluster");
        await Assert.That(detail.NodeShellNoticeIsWarning).IsTrue();

        detail.DismissNodeShellNoticeCommand.Execute(null);
        await Assert.That(detail.HasNodeShellNotice).IsFalse();
    }

    /// <summary>
    /// The node shell's command is kubectl debug node with the exec pane's pinned debug image —
    /// one image chosen by this app for a user's cluster, chosen in one place.
    /// </summary>
    [Test]
    public async Task TheNodeShellUsesThePinnedDebugImage()
    {
        await Assert.That(TerminalHandoff.NodeShellCommand("demo-worker-1").Summary)
            .IsEqualTo($"kubectl debug node/demo-worker-1 -it --image={DebugContainers.DefaultImage}");
    }

    // --------------------------------------------------------------------- palette

    /// <summary>
    /// The palette offers both hand-offs on the rows they apply to — "Exec in my terminal" on a
    /// pod, "Node shell in my terminal" on a node — and reports in the tab's own terminal notice.
    /// Driven on the demo tab, which refuses in place, so running the row starts nothing.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task ThePaletteOffersBothHandOffsOnTheRowsTheyApplyTo()
    {
        TestObjects.RedirectStores();
        using var shell = new MainWindowViewModel();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        shell.Tabs.Add(tab);
        shell.SelectedTab = tab;

        PaletteItem? Find(string title)
        {
            shell.Palette.Open(title);
            return shell.Palette.FilteredItems.FirstOrDefault(i => i.Title == title);
        }

        tab.SelectKindCommand.Execute(tab.SidebarSections.SelectMany(s => s.Kinds).First(k => k.Descriptor is { Group: "", Kind: "Pod" }));
        tab.SelectedRow = tab.Rows.First();
        var exec = Find("Exec in my terminal");
        await Assert.That(exec).IsNotNull();
        await Assert.That(Find("Node shell in my terminal")).IsNull();

        exec!.Execute!();
        await WaitFor(() => tab.TerminalNotice?.Contains("demo cluster") == true);
        await Assert.That(tab.TerminalNoticeIsWarning).IsTrue();

        tab.SelectKindCommand.Execute(tab.SidebarSections.SelectMany(s => s.Kinds).First(k => k.Descriptor is { Group: "", Kind: "Node" }));
        tab.SelectedRow = tab.Rows.First();
        await Assert.That(Find("Node shell in my terminal")).IsNotNull();
        await Assert.That(Find("Exec in my terminal")).IsNull();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }
    }
}
