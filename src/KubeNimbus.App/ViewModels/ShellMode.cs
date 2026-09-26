namespace KubeNimbus.App.ViewModels;

/// <summary>
/// The two ways into a cluster. The order is the command bar's segmented control's, and its
/// index — do not reorder without changing <see cref="MainWindowViewModel.ModeIndex"/>.
/// </summary>
public enum ShellMode
{
    /// <summary>The list of applications with their health and a one-line reason.</summary>
    Applications,

    /// <summary>The kinds, lists and inspector dock — the explorer this app started as.</summary>
    Resources,
}
