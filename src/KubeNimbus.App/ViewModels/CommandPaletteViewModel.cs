using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KubeNimbus.App.ViewModels;

public sealed record PaletteItem(string Title, string Subtitle, string IconKey, Action Execute);

/// <summary>Ctrl/Cmd+K palette: filters a caller-supplied action list by substring match on title/subtitle.</summary>
public sealed partial class CommandPaletteViewModel(Func<IEnumerable<PaletteItem>> itemSource) : ObservableObject
{
    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _query = "";

    public ObservableCollection<PaletteItem> FilteredItems { get; } = [];

    [ObservableProperty]
    private PaletteItem? _selectedItem;

    partial void OnQueryChanged(string value) => Refresh();

    public void Open()
    {
        IsOpen = true;
        Query = "";
        Refresh();
    }

    public void Close() => IsOpen = false;

    private void Refresh()
    {
        FilteredItems.Clear();
        var q = Query.Trim();
        var items = itemSource();
        var matches = string.IsNullOrEmpty(q)
            ? items
            : items.Where(i =>
                i.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || i.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase));

        foreach (var item in matches.Take(50))
        {
            FilteredItems.Add(item);
        }

        SelectedItem = FilteredItems.FirstOrDefault();
    }

    public void ExecuteSelected()
    {
        SelectedItem?.Execute();
        Close();
    }

    public void MoveSelection(int delta)
    {
        if (FilteredItems.Count == 0)
        {
            return;
        }

        var index = SelectedItem is null ? 0 : FilteredItems.IndexOf(SelectedItem);
        index = Math.Clamp(index + delta, 0, FilteredItems.Count - 1);
        SelectedItem = FilteredItems[index];
    }
}
