using KubeNimbus.App.ViewModels;

namespace KubeNimbus.App.Tests;

/// <summary>
/// UI rule 17 as revised: which mutating actions fire on their click and which arm a
/// confirm, and what the strip shows for one that fired. The split is the rule itself, so it
/// is pinned kind by kind — a kind moved across the line by accident is a destructive verb
/// one click away, or a reversible one behind a question nobody needs.
/// </summary>
public class RowActionClickRuleTests
{
    /// <summary>Reversible by their twin, or only moving the cluster toward what is declared.</summary>
    [Test]
    [Arguments(RowActionKind.Cordon)]
    [Arguments(RowActionKind.Uncordon)]
    [Arguments(RowActionKind.Suspend)]
    [Arguments(RowActionKind.ArgoSync)]
    [Arguments(RowActionKind.ArgoRefresh)]
    public async Task Fires_on_its_click(RowActionKind kind)
    {
        await Assert.That(RowActionViewModel.FiresOnClick(kind)).IsTrue();
    }

    /// <summary>
    /// Destroys, disrupts or cannot be taken back — or, for scale, needs a number first.
    /// </summary>
    [Test]
    [Arguments(RowActionKind.Delete)]
    [Arguments(RowActionKind.Drain)]
    [Arguments(RowActionKind.Restart)]
    [Arguments(RowActionKind.ArgoSyncPrune)]
    [Arguments(RowActionKind.Trigger)]
    [Arguments(RowActionKind.Resume)]
    [Arguments(RowActionKind.Scale)]
    public async Task Asks_first(RowActionKind kind)
    {
        await Assert.That(RowActionViewModel.FiresOnClick(kind)).IsFalse();
    }

    /// <summary>
    /// An action that fired on its click and was refused has no prompt to go back to, so the
    /// refusal is its answer and Close is reachable beside it. Sent to an address nothing
    /// listens on, so the failure is a real one from the real command path.
    /// </summary>
    [Test]
    public async Task A_refused_one_click_action_ends_on_its_refusal_with_close()
    {
        var action = new RowActionViewModel(
            RowActionKind.Cordon, TestObjects.OfflineClient(), TestObjects.NodeDescriptor, null, "worker-1");

        action.RunNow();
        if (action.ConfirmCommand.ExecutionTask is { } running)
        {
            await running;
        }

        await Assert.That(action.FiredOnClick).IsTrue();
        await Assert.That(action.IsError).IsTrue();
        await Assert.That(action.IsDone).IsTrue();
        await Assert.That(action.IsPromptVisible).IsFalse();
        await Assert.That(action.IsQuestionVisible).IsFalse();
        await Assert.That(action.Message).StartsWith("Cordon failed:");
    }

    /// <summary>A refused confirm keeps its prompt, so the same strip can try again.</summary>
    [Test]
    public async Task A_refused_confirm_keeps_its_prompt()
    {
        var action = new RowActionViewModel(
            RowActionKind.Delete, TestObjects.OfflineClient(), TestObjects.ConfigMapDescriptor, "payments", "settings");

        await action.ConfirmCommand.ExecuteAsync(null);

        await Assert.That(action.IsError).IsTrue();
        await Assert.That(action.IsDone).IsFalse();
        await Assert.That(action.IsPromptVisible).IsTrue();
        await Assert.That(action.IsQuestionVisible).IsTrue();
    }
}
