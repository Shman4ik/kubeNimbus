using KubeNimbus.Core.Commands;

namespace KubeNimbus.App;

/// <summary>
/// Tooltips that name a key the catalog has no entry of its own for — a log pane's search
/// box answers Enter, Shift+Enter and Alt+R/C/P itself — spelled for the live scheme
/// ("Alt+R" or "⌥R") by the rule the catalog's gesture notes follow
/// (<see cref="CommandDescriptor.SpellNote"/>). Properties rather than constants, read when a
/// view binds them with <c>x:Static</c>, so a pane opened after a scheme change spells the new one.
/// </summary>
public static class KeyTips
{
    public static string PreviousMatch => Spell("Previous (earlier) match — {key:Shift+Enter}");

    public static string NextMatch => Spell("Next (later) match — {key:Enter}");

    public static string PinSearch => Spell("Pin this search as a coloured highlight and search for something else — {key:Alt+P}");

    public static string MatchCase => Spell("Match case — {key:Alt+C}");

    public static string RegularExpression => Spell("Regular expression — {key:Alt+R}");

    public static string Spell(string note) => CommandDescriptor.SpellNote(note, Hotkeys.Scheme);
}
