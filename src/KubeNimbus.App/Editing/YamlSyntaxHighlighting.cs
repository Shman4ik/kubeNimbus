using System.Xml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace KubeNimbus.App.Editing;

/// <summary>
/// AvaloniaEdit ships highlighting definitions for C#/JSON/XML/etc but not YAML —
/// this loads a small hand-written one (see Assets/Yaml-Mode.xshd) from the
/// embedded resource. Loaded once via a static field initializer (CLR-guaranteed
/// thread-safe, unlike a manual `??=` lazy-init check); the XML-based .xshd
/// loader does its own manual parsing (no reflection-based serialization), so
/// this stays NativeAOT/trim-safe.
/// </summary>
public static class YamlSyntaxHighlighting
{
    public static IHighlightingDefinition Instance { get; } = Load();

    private static IHighlightingDefinition Load()
    {
        using var stream = typeof(YamlSyntaxHighlighting).Assembly.GetManifestResourceStream("Yaml-Mode.xshd")
            ?? throw new InvalidOperationException("Embedded Yaml-Mode.xshd resource not found.");
        using var reader = XmlReader.Create(stream);
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
