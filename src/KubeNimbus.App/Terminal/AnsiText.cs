using System.Text.RegularExpressions;

namespace KubeNimbus.App.Terminal;

/// <summary>
/// Strips ANSI escape sequences (SGR color codes, cursor movement, OSC titles)
/// from exec/log output so raw control bytes don't show up as literal garbage
/// in the line-oriented terminal — this is not a color-rendering terminal
/// emulator, just enough to keep plain text readable. A sequence split across
/// two read buffers can leak through uncommonly; acceptable for a line-oriented
/// terminal that isn't trying to be a full PTY renderer.
/// </summary>
public static partial class AnsiText
{
    [GeneratedRegex(@"\x1B(\[[0-9;?]*[a-zA-Z]|\][^\x07\x1B]*(\x07|\x1B\\)|[()][A-Za-z0-9])")]
    private static partial Regex EscapeSequencePattern();

    public static string StripEscapeCodes(string text) => EscapeSequencePattern().Replace(text, "");
}
