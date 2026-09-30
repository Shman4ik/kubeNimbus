using AvaloniaEdit;

namespace KubeNimbus.App.Editing;

/// <summary>
/// What every AvaloniaEdit editor in the app is given the same way: the YAML editor, and
/// the Helm release's values and manifest viewers. A new <c>TextEditor</c> calls one of
/// these next to <see cref="YamlSyntaxHighlighting.Attach"/>. pgNimbus has the same helper
/// for its SQL editor and cell inspector, where the bug was found first (2026-09-30).
/// </summary>
public static class EditorDefaults
{
    /// <summary>
    /// Turns off AvaloniaEdit's link rendering. It is on by default and draws every URL and
    /// e-mail address in pure Blue over whatever the highlighter said, so an Argo
    /// <c>repoURL</c>, a link in an annotation or a chart maintainer's address came out blue
    /// in the middle of a string-coloured value, and a Ctrl+click on it opened the browser
    /// or the mail client. Nothing in a manifest is a link.
    /// </summary>
    public static void Apply(TextEditor editor)
    {
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
    }

    /// <summary>
    /// <see cref="Apply"/>, for text that is only read. AvaloniaEdit's default also lets the
    /// view scroll all but one line past the end of the document, which put a scroll bar
    /// beside a values file that fits. Not for the YAML editor, even while it is read-only
    /// (a deleted object, the demo cluster): it is the working editor, and room below the
    /// last line is where the next line gets typed.
    /// </summary>
    public static void ApplyViewer(TextEditor editor)
    {
        Apply(editor);
        editor.Options.AllowScrollBelowDocument = false;
    }
}
