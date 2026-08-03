using System.Text;

namespace KubeNimbus.App.Terminal;

/// <summary>
/// One incremental change to the terminal's text, produced by
/// <see cref="TerminalOutputBuffer.Drain"/> and applied by the view to its
/// document. Deliberately *not* "here is the whole buffer again": re-materializing
/// a 200 000-char string per read (and making the text control re-shape all of it)
/// is what used to melt the UI thread on <c>cat</c> of a large file.
/// </summary>
/// <param name="Cleared">The scrollback was wiped (<c>ESC[2J</c>, or a local Clear) — drop everything first.</param>
/// <param name="CommittedText">Lines completed since the last drain. Empty or newline-terminated.</param>
/// <param name="CurrentLine">
/// The line currently being written, which is not yet terminated. It replaces
/// whatever the view last rendered as the tail, so <c>\r</c> overwrites and
/// backspace edits land as a rewrite of one line rather than a new line.
/// </param>
public readonly record struct TerminalUpdate(bool Cleared, string CommittedText, string CurrentLine);

/// <summary>
/// A deliberately small terminal model for the exec pane: enough of the C0 control
/// codes and ANSI sequences that a real shell's output reads correctly, and nothing
/// more. It is not a VT emulator — there is no addressable screen grid, no colour
/// attributes and no alternate buffer; the model is a scrollback of finished lines
/// plus one line being written, with a cursor column inside it.
///
/// What that buys, and why each piece is here:
/// <list type="bullet">
/// <item><c>\r</c> moves the cursor to column 0 <b>without</b> starting a line, so a
/// <c>\r</c>-only progress bar (curl, pip, docker pull) overwrites itself instead of
/// writing hundreds of lines and blowing through the scrollback cap.</item>
/// <item><c>\b</c> steps back a column, so shell line editing echoes as editing
/// rather than as literal garbage.</item>
/// <item><c>ESC[2J</c>/<c>ESC[3J</c> clear, so <c>clear</c> works.</item>
/// <item><c>ESC=</c>/<c>ESC&gt;</c> (keypad mode, which most shells emit around every
/// prompt) are consumed instead of leaking a literal <c>=</c>/<c>&gt;</c>.</item>
/// <item>BEL and every other unhandled control byte are dropped.</item>
/// <item>Parsing is a character-by-character state machine held in fields, so a
/// sequence split across two socket reads is resumed rather than leaked. The old
/// regex-per-chunk approach could not do this, and the leak was permanent because
/// the fragment landed in the persistent buffer.</item>
/// </list>
/// SGR colour is parsed as "a sequence to consume" and discarded: rendering it would
/// need styled runs, which is exactly the per-chunk re-materialization this class
/// exists to avoid.
/// </summary>
public sealed class TerminalOutputBuffer
{
    /// <summary>Scrollback cap, in characters (mirrors the 4000-line cap on pod logs).</summary>
    public const int MaxChars = 200_000;

    /// <summary>Trim only once this much has accumulated past the cap — an O(n) memmove per read was the other half of the melt.</summary>
    private const int TrimSlack = 20_000;

    /// <summary>Give up on a control sequence that never terminates rather than swallowing the stream.</summary>
    private const int MaxSequenceLength = 512;

    private const int TabWidth = 8;

    private enum State
    {
        Text,
        Escape,
        Csi,
        Osc,
        OscEscape,
        Charset,
    }

    private readonly StringBuilder _scrollback = new();
    private readonly StringBuilder _pending = new();
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _sequence = new();

    private State _state = State.Text;
    private int _cursor;
    private bool _cleared;
    private bool _dirty;

    /// <summary>Feeds one decoded chunk of the pod's stdout. Not thread-safe; the caller serializes.</summary>
    public void Feed(string text)
    {
        foreach (var c in text)
        {
            Consume(c);
        }
    }

    /// <summary>Wipes the buffer and the parser state (local Clear, or a whole-buffer replace).</summary>
    public void Reset()
    {
        ClearAll();
        _sequence.Clear();
        _state = State.Text;
    }

    /// <summary>
    /// Returns the change since the last call, or null when nothing moved. Clears
    /// the pending delta, so exactly one consumer may drain.
    /// </summary>
    public TerminalUpdate? Drain()
    {
        if (!_dirty)
        {
            return null;
        }

        var update = new TerminalUpdate(_cleared, _pending.ToString(), _line.ToString());
        _pending.Clear();
        _cleared = false;
        _dirty = false;
        return update;
    }

    /// <summary>The whole buffer — for priming a freshly attached view and for Copy. Not on the hot path.</summary>
    public string Snapshot() => _scrollback.Length == 0
        ? _line.ToString()
        : string.Concat(_scrollback.ToString(), _line.ToString());

    private void Consume(char c)
    {
        switch (_state)
        {
            case State.Text:
                ConsumeText(c);
                break;
            case State.Escape:
                ConsumeEscape(c);
                break;
            case State.Csi:
                ConsumeCsi(c);
                break;
            case State.Osc:
                ConsumeOsc(c);
                break;
            case State.OscEscape:
                // Inside an OSC string, ESC \ is the terminator; anything else was literal.
                _state = c == '\\' ? State.Text : State.Osc;
                break;
            case State.Charset:
                // ESC ( B and friends designate a character set: one byte, dropped.
                _state = State.Text;
                break;
        }
    }

    private void ConsumeText(char c)
    {
        switch (c)
        {
            case '':
                _state = State.Escape;
                _sequence.Clear();
                return;
            case '\n':
                CommitLine();
                return;
            case '\r':
                // Column 0 of the *same* line: the next writes overwrite it.
                _cursor = 0;
                _dirty = true;
                return;
            case '\b':
                if (_cursor > 0)
                {
                    _cursor--;
                    _dirty = true;
                }

                return;
            case '\t':
                Tab();
                return;
            case '\a':
            case '\f':
            case '\v':
                return;
            default:
                if (c < ' ' || c == '')
                {
                    // Unhandled C0 / DEL: dropping beats rendering a replacement box.
                    return;
                }

                Put(c);
                return;
        }
    }

    private void ConsumeEscape(char c)
    {
        switch (c)
        {
            case '[':
                _state = State.Csi;
                _sequence.Clear();
                return;
            case ']':
                _state = State.Osc;
                _sequence.Clear();
                return;
            case '(':
            case ')':
            case '*':
            case '+':
                _state = State.Charset;
                return;
            case '':
                // ESC ESC — stay armed rather than emitting a stray escape.
                return;
            default:
                // ESC =, ESC >, ESC 7/8/M/D/E/c … all single-character and all
                // irrelevant to a scrollback. Keypad mode is the one that matters:
                // shells emit it around every prompt, and stripping only CSI left
                // literal "=" and ">" in the output.
                _state = State.Text;
                return;
        }
    }

    private void ConsumeCsi(char c)
    {
        if (c is >= '@' and <= '~')
        {
            ApplyCsi(_sequence.ToString(), c);
            _sequence.Clear();
            _state = State.Text;
            return;
        }

        _sequence.Append(c);
        if (_sequence.Length > MaxSequenceLength)
        {
            _sequence.Clear();
            _state = State.Text;
        }
    }

    private void ConsumeOsc(char c)
    {
        switch (c)
        {
            case '\a':
                _sequence.Clear();
                _state = State.Text;
                return;
            case '':
                _state = State.OscEscape;
                return;
            default:
                _sequence.Append(c);
                if (_sequence.Length > MaxSequenceLength)
                {
                    _sequence.Clear();
                    _state = State.Text;
                }

                return;
        }
    }

    private void ApplyCsi(string parameters, char final)
    {
        switch (final)
        {
            case 'J':
                // Erase in display. 2/3 = everything, which is what `clear` sends.
                // 0 (erase below) can only mean "the rest of this line" here.
                if (parameters is "2" or "3")
                {
                    ClearAll();
                }
                else
                {
                    EraseFromCursor();
                }

                return;
            case 'K':
                switch (parameters)
                {
                    case "1":
                        EraseToCursor();
                        return;
                    case "2":
                        EraseLine();
                        return;
                    default:
                        EraseFromCursor();
                        return;
                }

            case 'H':
            case 'f':
            case 'G':
                // No addressable grid in a scrollback; column 1 of the current line
                // is the closest honest interpretation of "go home".
                _cursor = 0;
                _dirty = true;
                return;
            case 'C':
                _cursor += Count(parameters);
                _dirty = true;
                return;
            case 'D':
                _cursor = Math.Max(0, _cursor - Count(parameters));
                _dirty = true;
                return;
            default:
                // SGR colour (m), cursor up/down, mode set/reset, device status …
                // consumed and dropped.
                return;
        }
    }

    private static int Count(string parameters) =>
        int.TryParse(parameters, out var value) && value > 0 ? value : 1;

    private void Put(char c)
    {
        PadTo(_cursor);
        if (_cursor < _line.Length)
        {
            _line[_cursor] = c;
        }
        else
        {
            _line.Append(c);
        }

        _cursor++;
        _dirty = true;
    }

    private void PadTo(int column)
    {
        while (_line.Length < column)
        {
            _line.Append(' ');
        }
    }

    private void Tab()
    {
        var next = ((_cursor / TabWidth) + 1) * TabWidth;
        PadTo(next);
        _cursor = next;
        _dirty = true;
    }

    private void CommitLine()
    {
        _scrollback.Append(_line).Append('\n');
        _pending.Append(_line).Append('\n');
        _line.Clear();
        _cursor = 0;
        _dirty = true;
        Trim();
    }

    private void EraseFromCursor()
    {
        if (_cursor < _line.Length)
        {
            _line.Length = _cursor;
            _dirty = true;
        }
    }

    private void EraseToCursor()
    {
        PadTo(_cursor);
        for (var i = 0; i < _cursor && i < _line.Length; i++)
        {
            _line[i] = ' ';
        }

        _dirty = true;
    }

    private void EraseLine()
    {
        _line.Clear();
        _cursor = 0;
        _dirty = true;
    }

    private void ClearAll()
    {
        _scrollback.Clear();
        _pending.Clear();
        _line.Clear();
        _cursor = 0;
        _cleared = true;
        _dirty = true;
    }

    private void Trim()
    {
        if (_scrollback.Length <= MaxChars + TrimSlack)
        {
            return;
        }

        var cut = _scrollback.Length - MaxChars;
        while (cut < _scrollback.Length && _scrollback[cut] != '\n')
        {
            cut++;
        }

        if (cut < _scrollback.Length)
        {
            cut++;
        }

        _scrollback.Remove(0, cut);
    }
}
