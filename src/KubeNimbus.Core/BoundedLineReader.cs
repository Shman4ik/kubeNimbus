using System.Buffers;
using System.Text;

namespace KubeNimbus.Core;

/// <summary>
/// Reads a byte stream one line at a time, with a cap on how long a line may be. It
/// replaces <see cref="StreamReader.ReadLineAsync(CancellationToken)"/> on the two streams a
/// cluster writes without a bound — a watch and a pod's log — where one line with no end
/// would otherwise be read into memory until the process ran out of it. A container's
/// stdout is written by whatever runs in it, so "one line with no end" is a few bytes of
/// shell away.
/// </summary>
/// <remarks>
/// <para>
/// Lines end where <see cref="StreamReader"/> ended them — at <c>\n</c>, <c>\r</c> or
/// <c>\r\n</c> — so a log that draws a progress bar with carriage returns is split the way it
/// always was. A UTF-8 byte order mark at the start of the stream is dropped, as
/// <see cref="StreamReader"/> did. Both terminators are ASCII, which UTF-8 never uses inside
/// a multi-byte character, so splitting on bytes before decoding is exact.
/// </para>
/// <para>
/// A line longer than the cap comes back as its first <c>maxLineBytes</c> bytes with
/// <see cref="Line.Truncated"/> set, as soon as the cap is reached — a line that never ends
/// must not hold up the ones before it. After that the log's reader drops the rest of the
/// line up to its terminator without keeping it, and the watch's stops reading. What to do
/// about the head is the caller's: the log pane shows it with a marker, and the watch treats
/// it as a broken frame.
/// </para>
/// </remarks>
public sealed class BoundedLineReader
{
    private const int ChunkSize = 16 * 1024;

    private readonly Stream _stream;
    private readonly int _maxLineBytes;
    private readonly byte[] _chunk = new byte[ChunkSize];
    private readonly ArrayBufferWriter<byte> _line = new();
    private int _chunkStart;
    private int _chunkEnd;
    private bool _afterCarriageReturn;
    private bool _atStart = true;
    private bool _ended;
    private readonly bool _discardOverflow;
    private bool _skipping;

    /// <param name="stream">The stream to read.</param>
    /// <param name="maxLineBytes">The longest line kept.</param>
    /// <param name="discardOverflow">
    /// True (the log stream): a line past the cap comes back as its head once its end has been
    /// read and dropped, and reading goes on. False (the watch): it comes back as soon as the
    /// cap is reached and the reader ends there — a frame with no end in sight is a stream to
    /// stop reading, not one to drain.
    /// </param>
    public BoundedLineReader(Stream stream, int maxLineBytes, bool discardOverflow = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineBytes, 1);
        _stream = stream;
        _maxLineBytes = maxLineBytes;
        _discardOverflow = discardOverflow;
    }

    /// <summary>One line, without its terminator.</summary>
    /// <param name="Bytes">The line's bytes, valid until the next read.</param>
    /// <param name="Truncated">The line ran past the cap; <paramref name="Bytes"/> is its head.</param>
    public readonly record struct Line(ReadOnlyMemory<byte> Bytes, bool Truncated)
    {
        public string Text => Encoding.UTF8.GetString(Bytes.Span);
    }

    /// <summary>The next line, or null at the end of the stream.</summary>
    public async ValueTask<Line?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        _line.Clear();
        var truncated = false;
        while (true)
        {
            if (_chunkStart == _chunkEnd)
            {
                if (_ended)
                {
                    return EndOfStream(truncated);
                }

                _chunkStart = 0;
                _chunkEnd = await _stream.ReadAsync(_chunk, cancellationToken).ConfigureAwait(false);

                if (_atStart)
                {
                    // A byte order mark can arrive a byte at a time, so the first three bytes
                    // are gathered before it is looked for.
                    _atStart = false;
                    while (_chunkEnd is > 0 and < 3)
                    {
                        var more = await _stream.ReadAsync(_chunk.AsMemory(_chunkEnd), cancellationToken).ConfigureAwait(false);
                        if (more == 0)
                        {
                            break;
                        }

                        _chunkEnd += more;
                    }

                    if (_chunkEnd >= 3 && _chunk[0] == 0xEF && _chunk[1] == 0xBB && _chunk[2] == 0xBF)
                    {
                        _chunkStart = 3;
                    }
                }

                if (_chunkEnd == 0)
                {
                    _ended = true;
                    return EndOfStream(truncated);
                }

                if (_chunkStart == _chunkEnd)
                {
                    continue; // the stream so far was only the byte order mark
                }
            }

            if (_afterCarriageReturn)
            {
                // The \n of a \r\n split across two reads: the line already ended at the \r.
                _afterCarriageReturn = false;
                if (_chunk[_chunkStart] == (byte)'\n')
                {
                    _chunkStart++;
                    continue;
                }
            }

            var span = _chunk.AsSpan(_chunkStart, _chunkEnd - _chunkStart);
            var terminator = span.IndexOfAny((byte)'\n', (byte)'\r');

            if (_skipping)
            {
                // The rest of a line already returned as cut: dropped, terminator included.
                if (terminator < 0)
                {
                    _chunkStart = _chunkEnd;
                    continue;
                }

                _skipping = false;
                ConsumeTerminator(span[terminator], terminator);
                continue;
            }
            var content = terminator < 0 ? span : span[..terminator];
            Append(content, ref truncated);

            if (truncated)
            {
                // The head goes back now — a line that never ends must not hold up what was
                // already read. Then either the reader ends here (the watch), or the next read
                // first drops everything up to this line's terminator (the log).
                if (_discardOverflow)
                {
                    _skipping = true;
                    _chunkStart += content.Length;
                }
                else
                {
                    _ended = true;
                    _chunkStart = _chunkEnd;
                }

                return new Line(_line.WrittenMemory, Truncated: true);
            }

            if (terminator < 0)
            {
                _chunkStart = _chunkEnd;
                continue;
            }

            ConsumeTerminator(span[terminator], terminator);
            return new Line(_line.WrittenMemory, truncated);
        }
    }

    /// <summary>Steps past the terminator at <paramref name="offset"/> into the current chunk, and the \n of a \r\n.</summary>
    private void ConsumeTerminator(byte terminator, int offset)
    {
        _chunkStart += offset + 1;
        if (terminator != (byte)'\r')
        {
            return;
        }

        if (_chunkStart < _chunkEnd)
        {
            if (_chunk[_chunkStart] == (byte)'\n')
            {
                _chunkStart++;
            }
        }
        else
        {
            _afterCarriageReturn = true;
        }
    }

    private Line? EndOfStream(bool truncated) =>
        _line.WrittenCount > 0 || truncated ? new Line(_line.WrittenMemory, truncated) : null;

    private void Append(ReadOnlySpan<byte> content, ref bool truncated)
    {
        var room = _maxLineBytes - _line.WrittenCount;
        if (content.Length > room)
        {
            truncated = true;
            content = content[..room];
        }

        if (!content.IsEmpty)
        {
            _line.Write(content);
        }
    }
}
