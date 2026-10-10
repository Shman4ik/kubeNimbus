namespace KubeNimbus.App.ViewModels;

/// <summary>
/// What the exec pane puts on the clipboard for a copy: the selection with the blank cells
/// at the end of each line trimmed and blank lines at the end dropped (#267).
/// </summary>
/// <remarks>
/// The terminal control's own selection is a rectangle of cells, so Select all → Copy put the
/// whole screen on the clipboard with every line padded with spaces to the terminal's width
/// (137 columns in the report) and the empty rows under the prompt as trailing blank lines.
/// Those cells are not text the program wrote, and every terminal emulator trims them on copy;
/// pasted into an issue or a chat, the padding was noise. Spaces inside a line, and leading
/// ones, are kept: indentation and column alignment are what the program printed. The line
/// separator the selection used is kept as well.
/// </remarks>
public static class ExecCopy
{
    /// <summary>The text to copy, or empty when the selection holds only blank cells.</summary>
    public static string Trim(string? selected)
    {
        if (string.IsNullOrEmpty(selected))
        {
            return "";
        }

        var separator = selected.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = selected.Split('\n');
        var count = lines.Length;
        for (var i = 0; i < count; i++)
        {
            // A cell nothing was written to can come back as NUL rather than a space.
            lines[i] = lines[i].TrimEnd(' ', '\r', '\0');
        }

        while (count > 0 && lines[count - 1].Length == 0)
        {
            count--;
        }

        return string.Join(separator, lines, 0, count);
    }
}
