using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// The lines a log pane renders, with range operations that raise one notification each.
/// </summary>
/// <remarks>
/// <see cref="ObservableCollection{T}"/> has no range operations, and a log pane needs two:
/// a flush appends a whole tick's worth of lines, and the scrollback trim drops the oldest
/// ones. Done one item at a time, the trim was <c>RemoveAt(0)</c> in a loop — an array shift
/// per line plus a notification per line to the list, the overview ruler and the view's
/// scroll-to-end — and on "Everything" against a pod with a long history a single flush
/// appended hundreds of thousands of lines and then removed all but the last few thousand
/// that way, which froze the window. Avalonia's items controls accept multi-item Add and
/// Remove notifications (its own <c>AvaloniaList.AddRange</c> raises them), so each range
/// operation here is one event.
/// </remarks>
public sealed class LogLineCollection : ObservableCollection<LogLineViewModel>
{
    private static readonly PropertyChangedEventArgs CountChanged = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");

    // Collection<T>() backs itself with a List<T>, which is what makes RemoveRange available.
    private List<LogLineViewModel> List => (List<LogLineViewModel>)Items;

    /// <summary>Appends <paramref name="lines"/> at the end with one Add notification.</summary>
    public void AddRange(IReadOnlyList<LogLineViewModel> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        CheckReentrancy();
        var start = List.Count;
        List.AddRange(lines);
        OnPropertyChanged(CountChanged);
        OnPropertyChanged(IndexerChanged);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add, lines as System.Collections.IList ?? lines.ToList(), start));
    }

    /// <summary>Removes the first <paramref name="count"/> lines with one Remove notification.</summary>
    public void RemoveFromFront(int count)
    {
        count = Math.Min(count, List.Count);
        if (count <= 0)
        {
            return;
        }

        CheckReentrancy();
        var removed = List.GetRange(0, count);
        List.RemoveRange(0, count);
        OnPropertyChanged(CountChanged);
        OnPropertyChanged(IndexerChanged);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Remove, removed, 0));
    }

    /// <summary>Replaces the whole contents with one Reset notification.</summary>
    public void ReplaceAll(IReadOnlyList<LogLineViewModel> lines)
    {
        CheckReentrancy();
        List.Clear();
        List.AddRange(lines);
        OnPropertyChanged(CountChanged);
        OnPropertyChanged(IndexerChanged);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
