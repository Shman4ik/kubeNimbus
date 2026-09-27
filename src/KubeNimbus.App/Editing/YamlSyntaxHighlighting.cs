using System.Text.RegularExpressions;
using System.Xml;
using Avalonia.Controls;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace KubeNimbus.App.Editing;

/// <summary>
/// AvaloniaEdit ships highlighting definitions for C#/JSON/XML/etc but not YAML —
/// this loads a small hand-written one (see Assets/Yaml-Mode.xshd) from the
/// embedded resource. Loaded once via static field initializers (CLR-guaranteed
/// thread-safe, unlike a manual `??=` lazy-init check); the XML-based .xshd
/// loader does its own manual parsing (no reflection-based serialization), so
/// this stays NativeAOT/trim-safe.
///
/// There are two palettes, one per theme. The .xshd carries VS Code's dark one, and
/// it was used on both: on the light theme numbers (#B5CEA8), anchors (#DCDCAA) and
/// keys (#4FC1FF) came out pale on white, so a replica count or a revision annotation —
/// the values someone opens the YAML to read — were the faintest text on the page. The
/// light palette is VS Code's Light+ equivalent for each role. It is applied to the same
/// grammar by rewriting the colour attributes before loading, not kept as a second
/// .xshd, so the two can never disagree about what is a key or a number.
/// </summary>
public static class YamlSyntaxHighlighting
{
    private static readonly Dictionary<string, string> LightPalette = new(StringComparer.Ordinal)
    {
        ["Comment"] = "#008000",
        ["Key"] = "#0451A5",
        ["String"] = "#A31515",
        ["Number"] = "#098658",
        ["Keyword"] = "#0000FF",
        ["Anchor"] = "#795E26",
        ["Document"] = "#6E6E6E",
    };

    public static IHighlightingDefinition Dark { get; } = Load(light: false);

    public static IHighlightingDefinition Light { get; } = Load(light: true);

    public static IHighlightingDefinition For(ThemeVariant? variant) =>
        variant == ThemeVariant.Light ? Light : Dark;

    /// <summary>
    /// Gives <paramref name="editor"/> the palette for its theme now and again whenever the
    /// theme changes, which the toggle in the command bar does under an open editor.
    /// </summary>
    public static void Attach(TextEditor editor)
    {
        editor.SyntaxHighlighting = For(editor.ActualThemeVariant);
        editor.ActualThemeVariantChanged += (_, _) => editor.SyntaxHighlighting = For(editor.ActualThemeVariant);
    }

    private static IHighlightingDefinition Load(bool light)
    {
        using var stream = typeof(YamlSyntaxHighlighting).Assembly.GetManifestResourceStream("Yaml-Mode.xshd")
            ?? throw new InvalidOperationException("Embedded Yaml-Mode.xshd resource not found.");
        using var text = new StreamReader(stream);
        var xml = text.ReadToEnd();
        if (light)
        {
            xml = Regex.Replace(xml, "<Color name=\"(?<name>\\w+)\" foreground=\"#[0-9A-Fa-f]{6}\"", match =>
                LightPalette.TryGetValue(match.Groups["name"].Value, out var colour)
                    ? $"<Color name=\"{match.Groups["name"].Value}\" foreground=\"{colour}\""
                    : match.Value);
        }

        using var reader = XmlReader.Create(new StringReader(xml));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
