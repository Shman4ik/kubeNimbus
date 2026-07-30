using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Shell root: multi-cluster context tabs (each a <see cref="ClusterTabViewModel"/>
/// with its own connection/sidebar/list/inspector state), the command palette,
/// and workspace persistence (tabs + theme, no credentials — CLAUDE.md rule #4).
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public ObservableCollection<ClusterContext> AvailableContexts { get; } = [];

    public ObservableCollection<ClusterTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private ClusterTabViewModel? _selectedTab;

    [ObservableProperty]
    private ClusterContext? _newTabContext;

    [ObservableProperty]
    private string _status = "Loading kubeconfig…";

    public CommandPaletteViewModel Palette { get; }

    [ObservableProperty]
    private bool _isShortcutsOpen;

    [RelayCommand]
    private void ToggleShortcuts() => IsShortcutsOpen = !IsShortcutsOpen;

    [RelayCommand]
    private void CloseShortcuts() => IsShortcutsOpen = false;

    public MainWindowViewModel()
    {
        Palette = new CommandPaletteViewModel(BuildPaletteItems);
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var contexts = await Kubeconfig.LoadContextsAsync();
            AvailableContexts.Clear();
            foreach (var ctx in contexts)
            {
                AvailableContexts.Add(ctx);
            }

            NewTabContext = AvailableContexts.FirstOrDefault();
            Status = AvailableContexts.Count == 0
                ? "No kubeconfig contexts found."
                : $"{AvailableContexts.Count} context(s) available.";

            await RestoreWorkspaceAsync();
        }
        catch (Exception ex)
        {
            Status = $"Failed to read kubeconfig: {ex.Message}";
        }
    }

    private async Task RestoreWorkspaceAsync()
    {
        var settings = WorkspaceStore.Load();
        foreach (var snapshot in settings.Tabs)
        {
            var match = AvailableContexts.FirstOrDefault(c =>
                c.Name == snapshot.ContextName && c.KubeconfigPath == snapshot.KubeconfigPath);
            if (match is not null)
            {
                await AddTabAsync(match);
            }
        }

        if (Tabs.Count == 0 && AvailableContexts.Count > 0)
        {
            await AddTabAsync(AvailableContexts[0]);
        }
    }

    [RelayCommand]
    private async Task AddNewTabAsync()
    {
        if (NewTabContext is { } context)
        {
            await AddTabAsync(context);
        }
    }

    private async Task AddTabAsync(ClusterContext context)
    {
        var tab = new ClusterTabViewModel(context);
        Tabs.Add(tab);
        SelectedTab = tab;
        SaveWorkspace();
        await tab.ConnectCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task CloseTabAsync(ClusterTabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        await tab.DisposeAsync();
        if (SelectedTab == tab)
        {
            SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        SaveWorkspace();
    }

    /// <summary>Drag-reorder support — called from the view's drag/drop handler.</summary>
    public void MoveTab(int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex || oldIndex < 0 || newIndex < 0 || oldIndex >= Tabs.Count || newIndex >= Tabs.Count)
        {
            return;
        }

        Tabs.Move(oldIndex, newIndex);
        SaveWorkspace();
    }

    private void SaveWorkspace()
    {
        var settings = WorkspaceStore.Load();
        var tabs = Tabs.Select(t => new TabSnapshot(t.Context.Name, t.Context.KubeconfigPath)).ToList();
        WorkspaceStore.Save(settings with { Tabs = tabs });
    }

    public void PersistTheme(string? theme)
    {
        var settings = WorkspaceStore.Load();
        WorkspaceStore.Save(settings with { Theme = theme });
    }

    private IEnumerable<PaletteItem> BuildPaletteItems()
    {
        foreach (var tab in Tabs)
        {
            yield return new PaletteItem($"Switch to {tab.Header}", "Cluster tab", "SwapHorizontalIconGeometry", () => SelectedTab = tab);
        }

        foreach (var ctx in AvailableContexts)
        {
            yield return new PaletteItem($"Open new tab: {ctx.Name}", "Connect", "PlusIconGeometry", () => _ = AddTabAsync(ctx));
        }

        if (SelectedTab is { IsConnected: true } connected)
        {
            yield return new PaletteItem(
                "Access review — my permissions",
                $"RBAC · {connected.SelectedNamespace}", "AccountMultipleIconGeometry",
                () => connected.OpenAccessReviewCommand.Execute(null));

            if (connected.SelectedRowAsSubject is { } subject)
            {
                yield return new PaletteItem(
                    $"Access review: {subject.Name}",
                    $"RBAC · ServiceAccount bindings", "AccountMultipleIconGeometry",
                    () => connected.OpenAccessReviewCommand.Execute(subject));
            }
        }

        if (SelectedTab is { } current)
        {
            foreach (var section in current.SidebarSections)
            {
                foreach (var kind in section.Kinds)
                {
                    // Same-named kinds from different API groups carry their group
                    // here too, or the palette shows two identical-looking entries.
                    var subtitle = kind.HasGroupLabel
                        ? $"{section.Title} · {kind.GroupLabel} · {current.Header}"
                        : $"{section.Title} · {current.Header}";
                    yield return new PaletteItem(kind.DisplayName, subtitle, section.IconKey,
                        () => current.SelectKindCommand.Execute(kind));
                }
            }
        }
    }
}
