using System.Text;

namespace KubeNimbus.Core.Commands;

/// <summary>
/// Renders <see cref="CommandCatalog"/> as the published keyboard-shortcut reference.
/// The generated file is checked in and verified by a test, so the docs cannot quietly
/// fall behind the app the way a hand-written table would — the test fails on any
/// difference, and regenerating is one environment variable.
/// </summary>
public static class ShortcutDocs
{
    /// <summary>Path of the generated page, relative to the repository root.</summary>
    public const string RelativePath = "docs/keyboard-shortcuts.md";

    private const string GeneratedBanner =
        "<!-- Generated from KubeNimbus.Core.Commands.CommandCatalog by ShortcutDocs.ToMarkdown(). " +
        "Do not edit by hand — run the Core tests with KUBENIMBUS_UPDATE_DOCS=1 to regenerate. -->";

    /// <summary>
    /// The full Markdown page. Both modifier schemes are rendered side by side so one
    /// page serves Windows/Linux and macOS readers, rather than the reader having to
    /// know which platform the page was generated on.
    /// </summary>
    public static string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Keyboard shortcuts");
        sb.AppendLine();
        sb.AppendLine(GeneratedBanner);
        sb.AppendLine();
        sb.AppendLine("Every shortcut below is also discoverable in the app: press <kbd>F1</kbd> for the");
        sb.AppendLine("cheat sheet, or <kbd>Ctrl</kbd>+<kbd>K</kbd> to search commands by name.");
        sb.AppendLine();
        sb.AppendLine("The **Windows / Linux** and **macOS** columns differ only in the primary");
        sb.AppendLine("modifier. That choice follows the platform by default and can be forced either");
        sb.AppendLine("way in Preferences → Shortcut modifier.");
        sb.AppendLine();

        foreach (var (category, rows) in CommandCatalog.CheatSheetSections())
        {
            sb.AppendLine($"## {Title(CommandCatalog.Label(category))}");
            sb.AppendLine();
            sb.AppendLine("| Action | Windows / Linux | macOS |");
            sb.AppendLine("| --- | --- | --- |");
            foreach (var row in rows)
            {
                var windows = row.ShortcutLabel("Ctrl") ?? "—";
                var mac = row.ShortcutLabel("Cmd") ?? "—";
                sb.AppendLine($"| {Escape(row.DisplayName)} | {Escape(windows)} | {Escape(mac)} |");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// The in-app headings are upper-case because they render as small-caps section
    /// labels; a Markdown H2 shouting at the reader is a different thing, so they are
    /// title-cased here.
    /// </summary>
    private static string Title(string upper) =>
        upper.Length == 0 ? upper : upper[0] + upper[1..].ToLowerInvariant();

    // Pipes would split a table cell; the catalog has none today, but a future title
    // containing one shouldn't silently corrupt the table.
    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
