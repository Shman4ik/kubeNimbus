using Avalonia.Input;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core.Commands;

namespace KubeNimbus.App.Tests;

/// <summary>
/// Cmd/Ctrl+T and Cmd/Ctrl+W: new and close cluster tab, as window bindings on every
/// platform and the macOS Cluster menu's key equivalents. They had no chord at all, so ⌘W
/// and ⌘T did nothing on a Mac.
///
/// <para>
/// <c>[NotInParallel]</c>: the scheme is process-global and the shell writes the workspace.
/// </para>
/// </summary>
[NotInParallel]
public class TabChordTests
{
    [Test]
    public async Task New_and_close_cluster_tab_are_window_bindings_on_t_and_w()
    {
        try
        {
            Nimbus.Ui.Hotkeys.Initialize("mac");
            var bindings = CommandBindings.WindowBindings().ToDictionary(b => b.Id, b => b.Gesture);
            await Assert.That(bindings[CommandId.NewClusterTab]).IsEqualTo(new KeyGesture(Key.T, KeyModifiers.Meta));
            await Assert.That(bindings[CommandId.CloseClusterTab]).IsEqualTo(new KeyGesture(Key.W, KeyModifiers.Meta));

            Nimbus.Ui.Hotkeys.Initialize("windows");
            bindings = CommandBindings.WindowBindings().ToDictionary(b => b.Id, b => b.Gesture);
            await Assert.That(bindings[CommandId.NewClusterTab]).IsEqualTo(new KeyGesture(Key.T, KeyModifiers.Control));
            await Assert.That(bindings[CommandId.CloseClusterTab]).IsEqualTo(new KeyGesture(Key.W, KeyModifiers.Control));

            // …and on Windows and Linux the exec terminal keeps ^T and ^W for its shell.
            await Assert.That(CommandBindings.ShellOwns(bindings[CommandId.CloseClusterTab])).IsTrue();
        }
        finally
        {
            Nimbus.Ui.Hotkeys.Initialize("auto");
        }
    }

    /// <summary>
    /// ⌘W closes the tab in front, then the next one, and on an empty strip it is disabled:
    /// it never reaches the window, whose closing would quit this one-window app.
    /// </summary>
    [Test]
    public async Task Close_cluster_tab_closes_the_tab_in_front_and_is_disabled_with_none()
    {
        TestObjects.RedirectStores();
        using var shell = new MainWindowViewModel();
        await shell.Initialization;
        var first = new ClusterTabViewModel(TestObjects.Context);
        var second = new ClusterTabViewModel(TestObjects.Context);
        shell.Tabs.Add(first);
        shell.Tabs.Add(second);
        shell.SelectedTab = second;

        var close = CommandBindings.Resolve(CommandId.CloseClusterTab, shell)!;
        await Assert.That(close.CanExecute(null)).IsTrue();

        await shell.CloseSelectedTabCommand.ExecuteAsync(null);
        await Assert.That(shell.Tabs).IsEquivalentTo([first]);
        await Assert.That(shell.SelectedTab).IsSameReferenceAs(first);

        await shell.CloseSelectedTabCommand.ExecuteAsync(null);
        await Assert.That(shell.Tabs).IsEmpty();
        await Assert.That(close.CanExecute(null)).IsFalse();
    }

    [Test]
    public async Task New_cluster_tab_opens_the_switcher()
    {
        TestObjects.RedirectStores();
        using var shell = new MainWindowViewModel();
        await shell.Initialization;

        CommandBindings.Resolve(CommandId.NewClusterTab, shell)!.Execute(null);

        await Assert.That(shell.Switcher.IsOpen).IsTrue();
    }
}
