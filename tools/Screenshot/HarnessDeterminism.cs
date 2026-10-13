using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace KubeNimbus.Screenshot;

/// <summary>
/// #360: two runs of one commit must produce the same PNGs (verification.md), and the
/// interaction scenarios did not. ux-keyboard-walk's light render came out in four different
/// versions over a few runs, each a couple of dozen pixels ±1 in one colour channel along rounded
/// edges, while two captures of one window a second apart were identical: the frame was settled,
/// and what differed was how it had been painted (see <see cref="RepaintWhole"/>).
/// </summary>
internal static class HarnessDeterminism
{
    private static readonly List<string> Differences = [];

    /// <summary>
    /// Every control's <c>Transitions</c> set to none, from a style added after all of the
    /// app's: a later style beats an earlier one (nimbusUi CLAUDE.md rule 7), and a style beats
    /// a control theme's setter. A property then takes its new value at once, which is the value
    /// the capture is meant to show.
    /// </summary>
    internal static void StopTransitions(Application application)
    {
        application.Styles.Add(new Style(x => x.Is<Control>())
        {
            Setters = { new Setter(Animatable.TransitionsProperty, null) },
        });

        // And the caret's blink, which is a wall-clock timer too: ux-log-search-keys' light render
        // had its caret column in one run and not in the next. A zero interval does not blink, so
        // a focused box always draws its caret.
        application.Styles.Add(new Style(x => x.Is<TextBox>())
        {
            Setters = { new Setter(TextBox.CaretBlinkIntervalProperty, TimeSpan.Zero) },
        });
    }

    /// <summary>
    /// Repaints the whole window in one pass before it is captured. The compositor redraws only
    /// what was invalidated, so a frame is a patchwork of passes clipped to different dirty
    /// rectangles, and an edge anti-aliased across a clip boundary comes out a shade different
    /// depending on which passes ran: the run-to-run differences in ux-keyboard-walk were all
    /// ±1 in a colour channel, along the demo bar's rounded edges and down a straight seam inside
    /// it where one pass's clip ended. Which rectangles a focus walk invalidates depends on
    /// timing; one repaint of everything does not.
    /// </summary>
    internal static void RepaintWhole(Window window)
    {
        // The window draws nothing itself (its background is a template part), so invalidating it
        // alone marked nothing dirty; every visual that draws is invalidated instead.
        window.InvalidateVisual();
        foreach (var visual in window.GetVisualDescendants())
        {
            visual.InvalidateVisual();
        }

        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// The scenarios rendered twice in one run: the interaction ones, which press keys, move
    /// the pointer or the focus before the capture, and so are the ones that differed.
    /// </summary>
    internal static bool RendersTwice(string name) => name.StartsWith("ux-", StringComparison.Ordinal);

    /// <summary>
    /// Records the second render of <paramref name="path"/> when it is not byte-identical to the
    /// first, and writes it beside the first as <c>*.second.png</c>.
    /// </summary>
    internal static void Compare(string path, byte[] first, byte[] second)
    {
        if (!first.AsSpan().SequenceEqual(second))
        {
            // Kept beside the first, so the two can be compared pixel by pixel.
            File.WriteAllBytes(Path.ChangeExtension(path, ".second.png"), second);
            Differences.Add(Path.GetFileName(path));
        }
    }

    internal static void ThrowIfAnyDiffered()
    {
        if (Differences.Count > 0)
        {
            throw new InvalidOperationException(
                $"Rendered twice in one run, these differed (#360): {string.Join(", ", Differences)}. A capture "
                + "taken while something is still moving lands at a different point of it on every run.");
        }
    }
}
