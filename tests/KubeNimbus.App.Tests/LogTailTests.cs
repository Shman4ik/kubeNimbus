using Avalonia;
using KubeNimbus.App.Views;

namespace KubeNimbus.App.Tests;

/// <summary>
/// #403: a following log pane on its last line, made shorter by the dock going back from
/// maximized to the split, keeps its top offset (a <c>ScrollViewer</c> does) and so stops showing
/// the tail; the next line then found it "scrolled up" and the follow detached for good.
/// <see cref="LogTail.ResizeLeftTheTail"/> is the test the views' ScrollChanged handler asks.
/// </summary>
public class LogTailTests
{
    // 2,000 px of lines. Maximized, the pane is 800 px tall and on the tail (offset 1,200).
    private static readonly Size Extent = new(600, 2000);

    /// <summary>
    /// The issue's sequence: maximized on the tail, back to a 300 px split with the offset
    /// unchanged. Mutation-checked: <c>ResizeLeftTheTail</c> returning false (the code before
    /// the fix, which had no resize half) leaves the pane 500 px above its last line.
    /// </summary>
    [Test]
    public async Task Shrinking_a_pane_that_was_on_the_tail_asks_for_the_tail_again()
    {
        var offset = new Vector(0, 1200);
        var viewport = new Size(600, 300);

        await Assert.That(LogTail.IsAtBottom(offset.Y, Extent.Height, viewport.Height)).IsFalse();
        await Assert.That(LogTail.ResizeLeftTheTail(offset, Extent, viewport, default, default, new Vector(0, -500))).IsTrue();
    }

    [Test]
    public async Task A_pane_the_reader_scrolled_up_is_left_where_it_is()
    {
        // Was 400 px above the tail before the shrink: not following the tail, so not moved.
        var offset = new Vector(0, 800);
        await Assert.That(LogTail.ResizeLeftTheTail(offset, Extent, new Size(600, 300), default, default, new Vector(0, -500))).IsFalse();
    }

    [Test]
    public async Task A_scroll_that_is_not_a_resize_is_never_answered_with_the_tail()
    {
        // The reader's wheel: the offset moved, the viewport did not. Their choice stands.
        var offset = new Vector(0, 1000);
        await Assert.That(LogTail.ResizeLeftTheTail(offset, Extent, new Size(600, 800), new Vector(0, -200), default, default)).IsFalse();
    }

    [Test]
    public async Task Growing_a_pane_on_the_tail_needs_nothing()
    {
        // Maximize: the viewport grows and the ScrollViewer clamps the offset, still on the tail.
        var offset = new Vector(0, 1200);
        await Assert.That(LogTail.ResizeLeftTheTail(offset, Extent, new Size(600, 800), new Vector(0, -500), default, new Vector(0, 500))).IsFalse();
    }
}
