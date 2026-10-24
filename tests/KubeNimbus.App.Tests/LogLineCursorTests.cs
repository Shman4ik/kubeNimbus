using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #236: a log pane is one Tab stop and its lines are none, so a keyboard user could not get
/// to one line to copy it, nor open a JSON line's fields. <see cref="LogLineCursor"/> is the
/// line the focused list's keys are on; the view binds the keys to it
/// (<c>LogSearchGestures.AttachLineKeys</c>) and the harness's <c>ux-log-line-keys</c> presses
/// them in a real pane.
/// </summary>
public class LogLineCursorTests
{
    private static List<LogLineViewModel> Lines(params string[] raw) =>
        raw.Select(r => new LogLineViewModel(r, showTimestamp: false)).ToList();

    [Test]
    public async Task The_first_key_lands_on_the_newest_line_and_the_arrows_move_from_there()
    {
        var lines = Lines("one", "two", "three", "four");
        var cursor = new LogLineCursor();

        await Assert.That(cursor.Move(lines, LogLineCursor.Step.Previous)).IsSameReferenceAs(lines[3]);
        await Assert.That(cursor.Move(lines, LogLineCursor.Step.Previous)).IsSameReferenceAs(lines[2]);
        await Assert.That(cursor.Move(lines, LogLineCursor.Step.Next)).IsSameReferenceAs(lines[3]);
        await Assert.That(cursor.Move(lines, LogLineCursor.Step.Next)).IsSameReferenceAs(lines[3]);
        await Assert.That(cursor.Move(lines, LogLineCursor.Step.First)).IsSameReferenceAs(lines[0]);
        await Assert.That(cursor.Move(lines, LogLineCursor.Step.Previous)).IsSameReferenceAs(lines[0]);
        await Assert.That(cursor.Move(lines, LogLineCursor.Step.PageDown, page: 2)).IsSameReferenceAs(lines[2]);
        await Assert.That(cursor.Move(lines, LogLineCursor.Step.PageUp, page: 5)).IsSameReferenceAs(lines[0]);
        await Assert.That(cursor.Move(lines, LogLineCursor.Step.Last)).IsSameReferenceAs(lines[3]);
    }

    [Test]
    public async Task Only_the_line_under_the_cursor_is_marked()
    {
        var lines = Lines("one", "two", "three");
        var cursor = new LogLineCursor();

        cursor.Move(lines, LogLineCursor.Step.Last);
        cursor.Move(lines, LogLineCursor.Step.Previous);

        await Assert.That(lines.Select(l => l.IsKeyLine)).IsEquivalentTo(new[] { false, true, false });
    }

    [Test]
    public async Task Copy_takes_the_line_as_drawn_and_nothing_once_it_has_left_the_pane()
    {
        var lines = Lines("2026-10-24T10:00:00Z GET /health 200", "2026-10-24T10:00:01Z GET /cart 500");
        var cursor = new LogLineCursor();

        await Assert.That(cursor.CopyText(lines)).IsNull();
        cursor.Move(lines, LogLineCursor.Step.Last);
        await Assert.That(cursor.CopyText(lines)).IsEqualTo(lines[1].DisplayText);

        // Filtered out by the search, or dropped by the buffer: the next key starts again at
        // the newest line rather than copying a line no longer on screen.
        var shown = new List<LogLineViewModel> { lines[0] };
        await Assert.That(cursor.CopyText(shown)).IsNull();
        await Assert.That(cursor.Move(shown, LogLineCursor.Step.Previous)).IsSameReferenceAs(lines[0]);
    }

    [Test]
    public async Task Enter_opens_and_closes_a_json_line_and_leaves_a_plain_one_alone()
    {
        var lines = Lines("""{"level":"error","msg":"payment declined"}""", "plain text");
        var cursor = new LogLineCursor();

        cursor.Move(lines, LogLineCursor.Step.Last);
        await Assert.That(cursor.ToggleJson(lines)).IsFalse();

        cursor.Move(lines, LogLineCursor.Step.Previous);
        await Assert.That(lines[0].IsJson).IsTrue();
        await Assert.That(cursor.ToggleJson(lines)).IsTrue();
        await Assert.That(lines[0].IsExpanded).IsTrue();
        await Assert.That(cursor.ToggleJson(lines)).IsTrue();
        await Assert.That(lines[0].IsExpanded).IsFalse();
    }

    [Test]
    public async Task No_lines_means_no_cursor()
    {
        var cursor = new LogLineCursor();

        await Assert.That(cursor.Move([], LogLineCursor.Step.Last)).IsNull();
        await Assert.That(cursor.Current).IsNull();
    }
}
