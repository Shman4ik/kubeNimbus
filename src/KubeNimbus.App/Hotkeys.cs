using System.Runtime.InteropServices;
using Avalonia.Input;

namespace KubeNimbus.App;

/// <summary>
/// Single source of truth for platform chord modifiers. No Ctrl gesture is
/// hardcoded in views: palette labels and the cheat sheet all derive from here,
/// so Cmd-on-macOS vs Ctrl-elsewhere stays consistent.
/// </summary>
public static class Hotkeys
{
    /// <summary>Cmd on macOS, Ctrl everywhere else.</summary>
    public static KeyModifiers Primary { get; } =
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? KeyModifiers.Meta : KeyModifiers.Control;

    public static readonly KeyGesture CommandPalette = new(Key.K, Primary);

    /// <summary>Human-readable label for a gesture, for palette rows and the cheat sheet.</summary>
    public static string Describe(KeyGesture gesture)
    {
        var mod = gesture.KeyModifiers.HasFlag(KeyModifiers.Meta) ? "Cmd"
            : gesture.KeyModifiers.HasFlag(KeyModifiers.Control) ? "Ctrl"
            : "";
        return string.IsNullOrEmpty(mod) ? gesture.Key.ToString() : $"{mod}+{gesture.Key}";
    }
}
