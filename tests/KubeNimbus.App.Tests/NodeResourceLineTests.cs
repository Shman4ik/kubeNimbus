using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// The node Overview card's "allocatable vs requested" rows, as the view model formats
/// them for the meter beside them.
///
/// <para>
/// Two of these states are invisible in a screenshot of a healthy node and are exactly
/// the ones a reader would be misled by. The pods line has no limit at all — <c>Limit:
/// null</c> is its normal case, not missing data — so it must render no marker and no
/// caption rather than an empty slot where the two rows above it have a figure. And a
/// limit past allocatable is ordinary overcommit, which is one of the things this card
/// exists to show: clamping it into the track would render an oversubscribed node as
/// exactly full, which is the wrong answer stated confidently.
/// </para>
/// </summary>
public class NodeResourceLineTests
{
    private static NodeResourceLineViewModel Cpu(double allocatable, double requested, double? limit) =>
        new("CPU",
            new NodeResourceLine(NodeResources.Cpu, allocatable, allocatable, requested, limit),
            NodeDetailTabViewModel.FormatCores);

    private static NodeResourceLineViewModel Pods(double allocatable, double count) =>
        new("Pods",
            new NodeResourceLine(NodeResources.Pods, allocatable, allocatable, count, Limit: null),
            static v => v.ToString("0", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// The pods row: no limit exists for it, so nothing about limits is rendered — no
    /// marker position for the meter, no caption, no dangling separator.
    /// </summary>
    [Test]
    public async Task The_pods_row_offers_no_limit_marker_and_no_limit_text()
    {
        var line = Pods(110, 24);

        await Assert.That(line.HasLimit).IsFalse();
        await Assert.That(line.LimitPercentValue).IsNull();
        await Assert.That(line.LimitSummaryText).IsEqualTo("");
        await Assert.That(line.RequestedPercentText).IsEqualTo("22%");
    }

    /// <summary>
    /// The marker's position is deliberately unclamped. The meter needs to know the limit
    /// is past the track's end so it can draw it as overcommit; a value clamped here
    /// would arrive as exactly 100 and render as a node whose limits fit precisely.
    /// </summary>
    [Test]
    public async Task A_limit_past_allocatable_is_reported_unclamped_and_flagged()
    {
        var line = Cpu(allocatable: 7.8, requested: 0.7, limit: 8.5);

        await Assert.That(line.LimitPercentValue).IsNotNull();
        await Assert.That(line.LimitPercentValue!.Value).IsGreaterThan(100d);
        await Assert.That(line.IsOvercommitted).IsTrue();
        await Assert.That(line.LimitSummaryText).IsEqualTo("limits 8.5 (109%)");
    }

    /// <summary>Ordinary undersubscription is not flagged, and the figure still prints.</summary>
    [Test]
    public async Task A_limit_inside_allocatable_is_not_flagged()
    {
        var line = Cpu(allocatable: 7.8, requested: 1.35, limit: 1);

        await Assert.That(line.IsOvercommitted).IsFalse();
        await Assert.That(line.LimitSummaryText).IsEqualTo("limits 1 (13%)");
        await Assert.That(line.LimitPercentValue!.Value).IsLessThan(100d);
    }

    /// <summary>
    /// The requested fill is the half that <em>does</em> clamp: a fill drawn past its own
    /// track says less than the percentage printed beside it already does. The printed
    /// percentage keeps the real figure.
    /// </summary>
    [Test]
    public async Task Requested_clamps_for_the_fill_and_keeps_its_real_figure_in_the_text()
    {
        var line = Cpu(allocatable: 4, requested: 5, limit: null);

        await Assert.That(line.RequestedPercentValue).IsEqualTo(100d);
        await Assert.That(line.RequestedPercentText).IsEqualTo("125%");
        await Assert.That(line.IsTight).IsTrue();
    }

    /// <summary>
    /// Overcommitted limits are not by themselves a tight node: the two flags answer
    /// different questions (is the scheduler out of room / may the pods collectively ask
    /// for more than exists), and colouring the row on the second would say a normally
    /// run cluster is in trouble.
    /// </summary>
    [Test]
    public async Task Overcommitted_limits_do_not_make_the_row_read_as_tight()
    {
        var line = Cpu(allocatable: 7.8, requested: 0.7, limit: 12);

        await Assert.That(line.IsOvercommitted).IsTrue();
        await Assert.That(line.IsTight).IsFalse();
    }

    /// <summary>A node that reported no allocatable has nothing to be a percentage of.</summary>
    [Test]
    public async Task An_unreported_allocatable_leaves_every_percentage_empty()
    {
        var line = new NodeResourceLineViewModel(
            "CPU",
            new NodeResourceLine(NodeResources.Cpu, Allocatable: null, Capacity: null, 1.2, 2),
            NodeDetailTabViewModel.FormatCores);

        await Assert.That(line.RequestedPercentText).IsEqualTo("—");
        await Assert.That(line.LimitPercentValue).IsNull();
        await Assert.That(line.LimitSummaryText).IsEqualTo("limits 2");
        await Assert.That(line.IsOvercommitted).IsFalse();
    }
}
