using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace KubeNimbus.App.Views;

/// <summary>
/// The two namespace pickers' shared reading of a click, so the Applications list and the
/// Resources list choose namespaces the same way: a click on a row chooses that namespace
/// alone and closes the picker, as the single-namespace picker always did; a click on the
/// row's box, or a Ctrl/Cmd+click anywhere on it, adds or removes it and keeps the picker
/// open. Space does the latter from the keyboard and Enter the former.
/// </summary>
public static class NamespacePickerGestures
{
    /// <summary>The line under the pickers' search box. The modifier comes from <see cref="Hotkeys"/> (UI rule 4).</summary>
    public static string Hint => $"Click for one · its box, {Hotkeys.PrimaryLabel}+click or Space to add more";

    /// <summary>The picker row a tap landed on, by its data, or null for a tap between rows.</summary>
    public static T? RowAt<T>(TappedEventArgs e) where T : class =>
        (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as T;

    /// <summary>True when the tap adds or removes rather than choosing alone: on the box, or with Ctrl/Cmd held.</summary>
    public static bool IsAdd(TappedEventArgs e) =>
        (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0
        || (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().Any(b => b.Classes.Contains("nsCheckHit")) == true;
}
