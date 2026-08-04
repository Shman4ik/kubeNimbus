using KubeNimbus.Core.Commands;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// One piece of a shortcut row: either a key cap ("Ctrl", "Enter") or the quiet
/// connective text between alternatives ("/", "Double-click").
/// </summary>
public sealed record ShortcutToken(string Text, bool IsKey);

/// <summary>One line of the cheat sheet: what it does, and the keys that do it.</summary>
public sealed record ShortcutRow(string Action, IReadOnlyList<ShortcutToken> Tokens);

/// <summary>A titled group of rows — "CLUSTERS", "RESOURCES", …</summary>
public sealed record ShortcutSection(string Title, IReadOnlyList<ShortcutRow> Rows);

/// <summary>
/// Projects <see cref="CommandCatalog"/> into the F1 cheat sheet. The overlay used to
/// render a hand-written flat array of (action, keys) strings, which meant a new
/// shortcut had to be remembered in three places and the modifier was baked into the
/// string; now it comes from the same list the key bindings and the published docs do,
/// grouped into sections, with each key drawn as its own cap.
///
/// <para>
/// Rebuilt rather than cached across a scheme change: the caps spell out Ctrl or Cmd,
/// so a sheet built once would keep showing the other platform's chord after someone
/// changed the preference.
/// </para>
/// </summary>
public sealed class ShortcutsViewModel
{
    public IReadOnlyList<ShortcutSection> Sections { get; } = Build();

    private static IReadOnlyList<ShortcutSection> Build()
    {
        var label = Hotkeys.PrimaryLabel;

        return CommandCatalog.CheatSheetSections()
            .Select(section => new ShortcutSection(
                CommandCatalog.Label(section.Category),
                section.Rows.Select(row => new ShortcutRow(row.DisplayName, Tokenize(row, label))).ToList()))
            .ToList();
    }

    private static IReadOnlyList<ShortcutToken> Tokenize(CommandDescriptor descriptor, string commandLabel)
    {
        var tokens = new List<ShortcutToken>(6);

        if (descriptor.Chord is { } chord)
        {
            tokens.AddRange(chord.Caps(commandLabel).Select(cap => new ShortcutToken(cap, IsKey: true)));
        }

        if (descriptor.AltChord is { } alt)
        {
            Separate();
            tokens.AddRange(alt.Caps(commandLabel).Select(cap => new ShortcutToken(cap, IsKey: true)));
        }

        if (descriptor.GestureNoteFor(commandLabel) is { Length: > 0 } note)
        {
            Separate();
            tokens.Add(new ShortcutToken(note, IsKey: false));
        }

        return tokens;

        void Separate()
        {
            if (tokens.Count > 0)
            {
                tokens.Add(new ShortcutToken("/", IsKey: false));
            }
        }
    }
}
