using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// A log pane's pinned highlights (<see cref="LogPin"/>). <see cref="Items"/> is replaced
/// rather than mutated on every change, so the lines and the ruler that bind it see a new
/// value and repaint — a mutated collection would not tell a text block's render to run.
/// </summary>
public sealed partial class LogPins : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAny), nameof(IsFull))]
    private IReadOnlyList<LogPin> _items = [];

    public bool HasAny => Items.Count > 0;

    public bool IsFull => Items.Count >= LogPin.Max;

    /// <summary>
    /// Pins <paramref name="text"/> with the search's own options. False when there is
    /// nothing to pin, the pattern does not parse, it is already pinned, or five are.
    /// </summary>
    public bool Add(string text, bool regex, bool matchCase)
    {
        text = text.Trim();
        if (text.Length == 0 || IsFull || Items.Any(p => p.Text == text)
            || LogQuery.Create(text, regex, matchCase, out _) is not { HasInclude: true } query)
        {
            return false;
        }

        var slot = Enumerable.Range(0, LogPin.Max).First(s => Items.All(p => p.Slot != s));
        Items = [.. Items, new LogPin(text, query, slot)];
        return true;
    }

    [RelayCommand]
    private void Remove(LogPin pin) => Items = [.. Items.Where(p => !ReferenceEquals(p, pin))];

    [RelayCommand]
    private void Clear() => Items = [];
}
