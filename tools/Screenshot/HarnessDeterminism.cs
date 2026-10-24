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

        // And the indeterminate ProgressBar (#388), the row action strip's busy bar, shown while a
        // drain runs: Fluent slides its two indicators across the track on an infinite keyframe
        // animation driven by the render clock, so cluster-tab-node-drain-running caught them at a
        // different place on every run. Avalonia 12 keeps the animation clock internal, so the
        // animation cannot be paused from here; the indicators are hidden instead, which leaves the
        // empty track, as the bar looks between the indicators' sweeps.
        foreach (var part in new[] { "IndeterminateProgressBarIndicator", "IndeterminateProgressBarIndicator2" })
        {
            application.Styles.Add(new Style(x => x.OfType<ProgressBar>().Class(":indeterminate").Template().OfType<Border>().Name(part))
            {
                Setters = { new Setter(Visual.IsVisibleProperty, false) },
            });
        }
    }

    private static readonly HashSet<string> Animated = [];

    /// <summary>
    /// Records a scenario whose window shows an indeterminate <see cref="ProgressBar"/> (#388), the
    /// one control in the app that animates for as long as it is on screen. Such a scenario is
    /// rendered twice on every run, not only with <c>--twice</c>, so a capture that stops being
    /// frozen fails the PR that unfroze it. There are few of them, so the second render is cheap.
    /// </summary>
    internal static void NoteAnimations(Window window, string name)
    {
        if (window.GetVisualDescendants().OfType<ProgressBar>()
            .Any(bar => bar.IsIndeterminate && bar.IsEffectivelyVisible))
        {
            Animated.Add(name);
        }
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
    /// Set by <c>--twice</c>: the nightly run (<c>.github/workflows/nightly-determinism.yml</c>)
    /// renders the interaction scenarios twice. A PR's smoke test renders each once, because the
    /// second render is a full rebuild of the scenario and cost about a minute per shard.
    /// </summary>
    internal static bool Twice { get; set; }

    /// <summary>
    /// The scenarios rendered twice: in a <c>--twice</c> run the interaction ones, which press
    /// keys, move the pointer or the focus before the capture, and so are the ones that differed;
    /// in every run the ones <see cref="NoteAnimations"/> found an animation in (#388).
    /// </summary>
    internal static bool RendersTwice(string name) =>
        (Twice && name.StartsWith("ux-", StringComparison.Ordinal)) || Animated.Contains(name);

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
