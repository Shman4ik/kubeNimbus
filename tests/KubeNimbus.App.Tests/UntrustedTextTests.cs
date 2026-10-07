using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// Text a cluster wrote, as the panes draw it: a bidi override or a zero-width character is
/// a visible marker wherever it is shown, searched and copied (B2-6), and a paste into the
/// exec pane is what XTerm.NET's own paste would send (B2-5; the gesture itself is
/// <c>ux-exec-paste</c> in the screenshot harness, which needs a real window).
/// </summary>
public class UntrustedTextTests
{
    [Test]
    public async Task A_log_line_shows_searches_and_copies_the_marker_not_the_override()
    {
        var line = new LogLineViewModel("2026-10-07T08:00:00.000Z payment \u202Edeniedevorppa", showTimestamp: false);

        await Assert.That(line.Message).IsEqualTo("payment ⟨U+202E⟩deniedevorppa");
        await Assert.That(line.RawLine).Contains("⟨U+202E⟩");
        await Assert.That(line.RawLine).DoesNotContain("\u202E");
        await Assert.That(line.Contains("U+202E")).IsTrue();
    }

    [Test]
    public async Task An_events_reason_object_and_message_show_the_markers_and_match_them()
    {
        using var document = JsonDocument.Parse("""
            {"apiVersion":"v1","kind":"Event","type":"Warning","reason":"Back\u200BOff",
             "message":"Back-off restarting \u2066failed\u2069 container",
             "metadata":{"name":"web.1","namespace":"shop"},
             "involvedObject":{"kind":"Pod","name":"web-1","namespace":"shop"}}
            """);
        var resource = new DynamicResource(document.RootElement.Clone());
        var row = new ResourceRowViewModel(resource);
        var pane = new EventRowViewModel(resource);

        await Assert.That(row.EventReason).IsEqualTo("Back⟨U+200B⟩Off");
        await Assert.That(row.EventMessage).IsEqualTo("Back-off restarting ⟨U+2066⟩failed⟨U+2069⟩ container");
        await Assert.That(row.Matches("U+2066")).IsTrue();
        await Assert.That(pane.Reason).IsEqualTo(row.EventReason);
        await Assert.That(pane.Message).IsEqualTo(row.EventMessage);
    }

    [Test]
    public async Task A_paste_drops_controls_and_ends_lines_with_Return()
    {
        await Assert.That(ExecPaste.Prepare("ls -la\n", bracketed: false)).IsEqualTo("ls -la\r");
        await Assert.That(ExecPaste.Prepare("a\r\nb\nc", bracketed: false)).IsEqualTo("a\rb\rc");
        await Assert.That(ExecPaste.Prepare("x\u001b[201~; id\u009b0m\u0007\u0000\t.", bracketed: false))
            .IsEqualTo("x[201~; id0m\t.");
        await Assert.That(ExecPaste.Prepare("\u001b\u0007", bracketed: true)).IsEqualTo("");
    }

    [Test]
    public async Task A_paste_to_a_shell_that_asked_for_brackets_cannot_end_them_early()
    {
        var sent = ExecPaste.Prepare("echo hi\u001b[201~rm -rf /\n", bracketed: true);

        await Assert.That(sent).IsEqualTo("\u001b[200~echo hi[201~rm -rf /\r\u001b[201~");
        await Assert.That(sent.IndexOf(ExecPaste.BracketEnd, StringComparison.Ordinal)).IsEqualTo(sent.Length - ExecPaste.BracketEnd.Length);
    }

    [Test]
    public async Task One_line_is_sent_as_typed_and_more_than_one_is_counted()
    {
        await Assert.That(ExecPaste.LineCount("ls")).IsEqualTo(1);
        await Assert.That(ExecPaste.LineCount("ls\r")).IsEqualTo(1);
        await Assert.That(ExecPaste.LineCount("a\rb")).IsEqualTo(2);
        await Assert.That(ExecPaste.LineCount("a\rb\r")).IsEqualTo(2);
        await Assert.That(ExecPaste.LineCount("")).IsEqualTo(0);
    }
}
