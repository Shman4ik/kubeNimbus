using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Tests;

/// <summary>
/// VER-19, the shell's half: <c>HotkeySchemeTests</c> pins that every projection of the
/// Ctrl/Cmd scheme rebuilds correctly <em>when asked</em>; these pin that the shell view
/// model is actually asked. Delete its <c>Hotkeys.Changed</c> subscription and the first
/// test goes red while every one of those stays green — which was the gap.
///
/// <para>
/// The window's half (<c>MainWindow</c> rebuilding its key bindings) needs a running
/// Avalonia application, which this project deliberately does not start; it is the
/// screenshot harness's <c>ux-hotkey-scheme</c> check, which presses the chords against a
/// real window.
/// </para>
///
/// <para>
/// <c>[NotInParallel]</c> for <c>HotkeySchemeTests</c>' reason: the scheme is
/// process-global, and every test restores "auto" in a finally.
/// </para>
/// </summary>
[NotInParallel]
public class ShellHotkeySchemeTests
{
    [Test]
    public async Task The_shell_rebuilds_its_cheat_sheet_and_switcher_tooltip_when_the_scheme_changes()
    {
        TestObjects.RedirectStores();
        Nimbus.Ui.Hotkeys.Initialize("windows");
        var shell = new MainWindowViewModel();
        try
        {
            var before = shell.Shortcuts;
            await Assert.That(HotkeySchemeTests.Caps(before, "Switch or open a cluster")).IsEqualTo("Ctrl, P");
            await Assert.That(shell.SwitcherTooltip).Contains("Ctrl");

            var raised = new List<string?>();
            shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            Nimbus.Ui.Hotkeys.Initialize("mac");

            // The F1 sheet is a new one, spelled for the new scheme…
            await Assert.That(ReferenceEquals(shell.Shortcuts, before)).IsFalse();
            await Assert.That(HotkeySchemeTests.Caps(shell.Shortcuts, "Switch or open a cluster")).IsEqualTo("Cmd, P");

            // …and the switcher tooltip is re-read because the shell said so (ENG-17), not
            // because its popup happens to re-evaluate a binding when it next opens.
            await Assert.That(raised).Contains(nameof(MainWindowViewModel.SwitcherTooltip));
            await Assert.That(shell.SwitcherTooltip).Contains("Cmd");
            await Assert.That(shell.SwitcherTooltip).DoesNotContain("Ctrl");
        }
        finally
        {
            shell.Dispose();
            Nimbus.Ui.Hotkeys.Initialize("auto");
        }
    }

    /// <summary>
    /// ENG-16: a disposed shell is off the static event. Before, the handler had no
    /// matching removal, so every shell the harness or a test built kept rebuilding its
    /// cheat sheet on every later scheme change, for the rest of the process.
    /// </summary>
    [Test]
    public async Task A_disposed_shell_no_longer_follows_the_scheme()
    {
        TestObjects.RedirectStores();
        Nimbus.Ui.Hotkeys.Initialize("windows");
        var shell = new MainWindowViewModel();
        try
        {
            var before = shell.Shortcuts;
            var raised = new List<string?>();
            shell.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            shell.Dispose();
            Nimbus.Ui.Hotkeys.Initialize("mac");

            // Filtered rather than "nothing raised": the shell's constructor starts its own
            // kubeconfig load, whose completion may land while this test runs.
            await Assert.That(ReferenceEquals(shell.Shortcuts, before)).IsTrue();
            await Assert.That(raised).DoesNotContain(nameof(MainWindowViewModel.Shortcuts));
            await Assert.That(raised).DoesNotContain(nameof(MainWindowViewModel.SwitcherTooltip));
        }
        finally
        {
            shell.Dispose(); // idempotent: removing a handler that is not there is a no-op
            Nimbus.Ui.Hotkeys.Initialize("auto");
        }
    }
}
