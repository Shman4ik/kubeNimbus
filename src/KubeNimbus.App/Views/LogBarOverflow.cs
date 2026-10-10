using Avalonia;
using Avalonia.Controls;

namespace KubeNimbus.App.Views;

/// <summary>
/// ENG-51: a log pane's bar gives way at a narrow window instead of running off the pane's
/// edge. Both panes put the search box, the problem counts, Range, Follow, Previous, Levels,
/// Copy and the <c>⋯</c> menu on one row (UI rule 10), and below about 1150px the right-hand
/// end — the menu with Clear and Save first — was simply cut off.
/// </summary>
/// <remarks>
/// <para>
/// What gives way, in order, is what is set once and then left alone: <b>Range</b>, then
/// <b>Levels</b>, then the filter's <b>context</b> chip, then <b>Copy</b>. Each moves into the <c>⋯</c> menu,
/// where a copy of it waits hidden, and only as many move as the row needs. The search box,
/// Follow, Previous (the CrashLoopBackOff gesture), the problem counts and the menu itself
/// always stay. Once everything movable has moved, the search box narrows from 230px,
/// down to 160px, where it still holds a query beside its own buttons.
/// </para>
/// <para>
/// Each movable control sits in a slot of its own, and the slot is what is hidden, never the
/// control: the controls carry visibility bindings of their own (the context chip only in
/// filter mode, Levels only outside the Applications page), and a local value set from here
/// would replace them. The width a hidden slot would take is the one it had when last shown.
/// </para>
/// </remarks>
internal sealed class LogBarOverflow
{
    /// <summary>The search box's width before anything gives way to keep it.</summary>
    internal const double ComfortableSearchWidth = 230;

    /// <summary>The narrowest the search box goes: the placeholder and a short query beside its buttons.</summary>
    internal const double SmallestSearchWidth = 160;

    private readonly Control _row;
    private readonly Control? _leading;
    private readonly TextBox _search;
    private readonly StackPanel _tools;
    private readonly (Panel Slot, Control InMenu)[] _movable;
    private readonly double[] _widths;

    /// <param name="row">The row the bar lives on.</param>
    /// <param name="leading">What sits left of the search box on that row (pod detail's tab strip), or null.</param>
    /// <param name="search">The search box, which takes the row's slack.</param>
    /// <param name="tools">The tools right of it.</param>
    /// <param name="movable">Each slot on the bar and its copy in the menu, in the order they give way.</param>
    internal LogBarOverflow(Control row, Control? leading, TextBox search, StackPanel tools,
        params (Panel Slot, Control InMenu)[] movable)
    {
        _row = row;
        _leading = leading;
        _search = search;
        _tools = tools;
        _movable = movable;

        // A first guess for a slot never shown yet (Range's fixed width, a short Levels label).
        _widths = [.. movable.Select(_ => 90.0)];

        row.SizeChanged += (_, _) => Update();
        tools.SizeChanged += (_, _) => Update();
        if (leading is not null) leading.SizeChanged += (_, _) => Update();
        foreach (var (slot, _) in movable)
        {
            // The control's own binding hiding it frees nothing on the bar; showing it again
            // may need a slot to move.
            foreach (var child in slot.Children)
            {
                child.PropertyChanged += (_, e) =>
                {
                    if (e.Property == Visual.IsVisibleProperty) Update();
                };
            }
        }
    }

    /// <summary>How many of the movable controls are in the menu now.</summary>
    internal int MovedCount => _movable.Count(m => !m.Slot.IsVisible);

    internal void Update()
    {
        var row = _row.Bounds.Width;
        if (row <= 0 || !_tools.IsEffectivelyVisible)
        {
            return;
        }

        var spacing = _tools.Spacing;
        for (var i = 0; i < _movable.Length; i++)
        {
            var slot = _movable[i].Slot;
            if (slot.IsVisible && slot.Bounds.Width > 0)
            {
                _widths[i] = slot.Bounds.Width + spacing;
            }
        }

        // The tools as they would be with every movable control back on the bar.
        var tools = _tools.DesiredSize.Width;
        for (var i = 0; i < _movable.Length; i++)
        {
            if (!_movable[i].Slot.IsVisible && Shows(i))
            {
                tools += _widths[i];
            }
        }

        var budget = row - (_leading?.Bounds.Width ?? 0) - _search.Margin.Left - _search.Margin.Right
            - ComfortableSearchWidth;
        var moved = 0;
        while (moved < _movable.Length && tools > budget + 0.5)
        {
            if (Shows(moved))
            {
                tools -= _widths[moved];
            }

            moved++;
        }

        // With everything movable in the menu and still not enough room, the search box gives
        // up the rest, down to SmallestSearchWidth. Its MinWidth is what holds it at 230 the
        // rest of the time: aligned left in its column, it is otherwise as wide as its text.
        var room = budget + ComfortableSearchWidth - tools;
        var minWidth = Math.Clamp(Math.Floor(room), SmallestSearchWidth, ComfortableSearchWidth);
        if (Math.Abs(_search.MinWidth - minWidth) > 0.5)
        {
            _search.MinWidth = minWidth;
        }

        for (var i = 0; i < _movable.Length; i++)
        {
            var onBar = i >= moved;
            if (_movable[i].Slot.IsVisible != onBar)
            {
                _movable[i].Slot.IsVisible = onBar;
            }

            if (_movable[i].InMenu.IsVisible == onBar)
            {
                _movable[i].InMenu.IsVisible = !onBar;
            }
        }
    }

    /// <summary>Whether the control in a slot is one its own binding currently shows.</summary>
    private bool Shows(int index) => _movable[index].Slot.Children.Any(c => c.IsVisible);
}
