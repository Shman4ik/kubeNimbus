using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> with range operations that raise one
/// notification each.
/// </summary>
/// <remarks>
/// <see cref="ObservableCollection{T}"/> has no range operations, and a list that is
/// rebuilt or trimmed wholesale pays one notification per item — to the items control,
/// and to everything else listening — plus an array shift per item for a removal at the
/// front. On a large cluster that is the difference between a list that updates and a
/// window that freezes: the log panes' trim alone raised 396,000 notifications for one
/// "Everything" flush. Avalonia's items controls accept multi-item Add and Remove
/// notifications (its own <c>AvaloniaList.AddRange</c> raises them), so each range
/// operation here is one event.
/// </remarks>
public class RangeObservableCollection<T> : ObservableCollection<T>
{
    private static readonly PropertyChangedEventArgs CountChanged = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");

    // Collection<T>() backs itself with a List<T>, which is what makes the range methods available.
    private List<T> List => (List<T>)Items;

    /// <summary>Appends <paramref name="items"/> at the end with one Add notification.</summary>
    public void AddRange(IReadOnlyList<T> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        CheckReentrancy();
        var start = List.Count;
        List.AddRange(items);
        RaiseCountChanged();
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add, items as IList ?? items.ToList(), start));
    }

    /// <summary>Removes the first <paramref name="count"/> items with one Remove notification.</summary>
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
        RaiseCountChanged();
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Remove, removed, 0));
    }

    /// <summary>Replaces the whole contents with one Reset notification.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        var had = List.Count;
        List.Clear();
        List.AddRange(items);
        if (had == 0 && List.Count == 0)
        {
            return;
        }

        RaiseCountChanged();
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private void RaiseCountChanged()
    {
        OnPropertyChanged(CountChanged);
        OnPropertyChanged(IndexerChanged);
    }
}

/// <summary>The lines a log pane renders.</summary>
public sealed class LogLineCollection : RangeObservableCollection<LogLineViewModel>;
