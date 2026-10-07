using System.Text;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// The line reader that replaced <see cref="StreamReader.ReadLineAsync(CancellationToken)"/>
/// on the watch and log streams: the same line endings, and a cap on a line's length.
/// </summary>
public class BoundedLineReaderTests
{
    /// <summary>A stream that hands out at most <paramref name="chunk"/> bytes per read, to split terminators across reads.</summary>
    private sealed class TrickleStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], cancellationToken);
    }

    private static async Task<List<string>> ReadAllAsync(string text, int maxLineBytes = 1024, int chunk = 1)
    {
        var reader = new BoundedLineReader(new TrickleStream(Encoding.UTF8.GetBytes(text), chunk), maxLineBytes);
        var lines = new List<string>();
        while (await reader.ReadLineAsync() is { } line)
        {
            lines.Add(line.Truncated ? "!" + line.Text : line.Text);
        }

        return lines;
    }

    [Test]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(4096)]
    public async Task Lines_end_where_StreamReader_ended_them(int chunk)
    {
        var text = "\uFEFFone\r\ntwo\nthree\rfour\r\n\r\nsix €\n\nlast";

        var expected = new List<string>();
        using (var reference = new StringReader(text[1..]))
        {
            while (await reference.ReadLineAsync() is { } line)
            {
                expected.Add(line);
            }
        }

        await Assert.That(string.Join("|", await ReadAllAsync(text, chunk: chunk))).IsEqualTo(string.Join("|", expected));
    }

    [Test]
    public async Task A_line_past_the_cap_comes_back_as_its_head_and_the_rest_of_it_is_dropped()
    {
        var lines = await ReadAllAsync("short\n" + new string('x', 50) + "\nnext\n", maxLineBytes: 10, chunk: 7);

        await Assert.That(string.Join("|", lines)).IsEqualTo("short|!xxxxxxxxxx|next");
    }

    [Test]
    public async Task The_watchs_reader_stops_at_the_cap_without_waiting_for_a_line_that_never_ends()
    {
        // A stream that sends the head of a frame and then nothing, for ever: the head must
        // come back at the cap rather than after an end that will not arrive.
        var pipe = new System.IO.Pipelines.Pipe();
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(new string('x', 64)));
        var reader = new BoundedLineReader(pipe.Reader.AsStream(), maxLineBytes: 10, discardOverflow: false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var line = await reader.ReadLineAsync(cts.Token);
        var (head, truncated) = (line!.Value.Text, line.Value.Truncated); // valid until the next read
        var after = await reader.ReadLineAsync(cts.Token);

        await Assert.That(truncated).IsTrue();
        await Assert.That(head).IsEqualTo("xxxxxxxxxx");
        await Assert.That(after).IsNull();
    }

    [Test]
    public async Task An_unterminated_last_line_is_still_a_line_and_an_empty_stream_has_none()
    {
        await Assert.That(string.Join("|", await ReadAllAsync("tail"))).IsEqualTo("tail");
        await Assert.That((await ReadAllAsync("")).Count).IsEqualTo(0);
    }
}
