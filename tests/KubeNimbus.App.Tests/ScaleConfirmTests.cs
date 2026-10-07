using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// Security block 3, B3-4: the scale strip states "from N to M" as the box changes, and warns
/// about the number in it — every pod stopping (prominently on production), and a jump of ten
/// times or more. Typing 300 for 3 used to get the same sentence as typing 4.
/// </summary>
public class ScaleConfirmTests
{
    private static readonly ResourceDescriptor Deployments =
        new("apps", "v1", "Deployment", "deployments", "deployment", Namespaced: true, ShortNames: [], Categories: [])
        {
            Subresources = ["scale"],
        };

    private static RowActionViewModel Scale(int? from, ClusterEnvironment environment = ClusterEnvironment.Development) =>
        new(RowActionKind.Scale, client: null, Deployments, "payments", "checkout", "payments-eu",
            replicas: from, environment: environment);

    [Test]
    public async Task The_question_says_from_and_to_and_follows_the_box()
    {
        var action = Scale(3);
        var raised = new List<string?>();
        action.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await Assert.That(action.Question).IsEqualTo("Scale Deployment/checkout in payments on payments-eu — it is already at 3");

        action.Replicas = 5;

        await Assert.That(action.Question).IsEqualTo("Scale Deployment/checkout in payments on payments-eu from 3 to 5");
        await Assert.That(raised).Contains(nameof(RowActionViewModel.Question));
    }

    [Test]
    public async Task With_no_starting_count_the_question_says_only_the_target()
    {
        var action = Scale(from: null);
        action.Replicas = 4;

        await Assert.That(action.Question).IsEqualTo("Scale Deployment/checkout in payments on payments-eu to 4");
        await Assert.That(action.HasScaleWarning).IsFalse();
    }

    /// <summary>The thresholds, at their edges: ten times or more, or ten or more from none.</summary>
    [Test]
    [Arguments(3, 29, false)]
    [Arguments(3, 30, true)]
    [Arguments(3, 300, true)]
    [Arguments(1, 9, false)]
    [Arguments(1, 10, true)]
    [Arguments(0, 9, false)]
    [Arguments(0, 10, true)]
    [Arguments(5, 4, false)]
    [Arguments(0, 0, false)]
    [Arguments(3, 0, true)]
    public async Task A_jump_or_a_stop_is_warned_about(int from, int to, bool warned)
    {
        var action = Scale(from);
        action.Replicas = to;

        await Assert.That(action.HasScaleWarning).IsEqualTo(warned);
    }

    [Test]
    public async Task A_ten_fold_jump_names_the_factor_and_the_current_count()
    {
        var action = Scale(3);
        action.Replicas = 300;

        await Assert.That(action.ScaleWarning).IsEqualTo("300 is 100× the current 3. Check the number before scaling.");
        await Assert.That(action.HasPlainScaleWarning).IsTrue();
        await Assert.That(action.IsScaleWarningProminent).IsFalse();
    }

    /// <summary>Zero off production is an ordinary warn line; on production it is the warn infoBar and says why.</summary>
    [Test]
    public async Task Scaling_to_zero_is_prominent_only_on_production()
    {
        var staging = Scale(3, ClusterEnvironment.Staging);
        staging.Replicas = 0;
        await Assert.That(staging.HasPlainScaleWarning).IsTrue();
        await Assert.That(staging.IsScaleWarningProminent).IsFalse();
        await Assert.That(staging.ScaleWarning).Contains("stops every pod");

        var production = Scale(3, ClusterEnvironment.Production);
        production.Replicas = 0;
        await Assert.That(production.IsScaleWarningProminent).IsTrue();
        await Assert.That(production.HasPlainScaleWarning).IsFalse();
        await Assert.That(production.ScaleWarning).StartsWith("This is a production cluster:");
        await Assert.That(production.ScaleWarning).Contains("stops every pod");
    }

    /// <summary>The warnings are a guard, never a block: the confirm stays live.</summary>
    [Test]
    public async Task A_warning_does_not_disable_the_confirm()
    {
        var action = new RowActionViewModel(
            RowActionKind.Scale, TestObjects.OfflineClient(), Deployments, "payments", "checkout", "payments-eu",
            replicas: 3, environment: ClusterEnvironment.Production);
        action.Replicas = 0;

        await Assert.That(action.IsScaleWarningProminent).IsTrue();
        await Assert.That(action.ConfirmCommand.CanExecute(null)).IsTrue();
    }

    [Test]
    public async Task Other_actions_carry_no_scale_warning()
    {
        var restart = new RowActionViewModel(
            RowActionKind.Restart, client: null, Deployments, "payments", "checkout", replicas: 3);

        await Assert.That(restart.ScaleWarning).IsNull();
        await Assert.That(restart.IsScaleWarningProminent).IsFalse();
    }
}
