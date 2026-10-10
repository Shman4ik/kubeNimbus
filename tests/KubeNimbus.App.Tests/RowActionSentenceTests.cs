using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// FEAT-77: the confirm strip reads as one sentence — verb, object, and for a scale the
/// current state — with the consequence under it. The view composes the sentence from
/// these parts, so they are what is pinned here; the layout half (the buttons beside the
/// text, the replica box as the sentence's blank) is the harness's
/// <c>LayoutChecks.ActionStripReadsAsOneBlock</c>.
/// </summary>
public class RowActionSentenceTests
{
    private static readonly ResourceDescriptor Deployment =
        new("apps", "v1", "Deployment", "deployments", "deployment", true, [], []) { Subresources = ["scale"] };

    private static RowActionViewModel Arm(RowActionKind kind, ResourceDescriptor? descriptor = null, string? ns = "payments", string cluster = "") =>
        new(kind, client: null, descriptor ?? Deployment, ns, "checkout-worker", cluster);

    [Test]
    public async Task The_scale_sentence_carries_the_current_state_once_it_has_been_read()
    {
        var action = Arm(RowActionKind.Scale);
        await Assert.That(action.Headline).IsEqualTo("Scale Deployment checkout-worker in payments to");

        action.SetCurrentScale(2, 1);

        await Assert.That(action.Headline)
            .IsEqualTo("Scale Deployment checkout-worker in payments from 2 replicas (1 running) to");
        await Assert.That(action.HasConsequence).IsFalse();
    }

    [Test]
    public async Task One_replica_is_singular_and_an_unreported_status_is_left_out()
    {
        var action = Arm(RowActionKind.Scale);
        action.SetCurrentScale(1, null);

        await Assert.That(action.HeadlineSuffix).IsEqualTo(" from 1 replica to");
    }

    /// <summary>The sentence and the clause it depends on change together, so a bound view redraws both.</summary>
    [Test]
    public async Task Reading_the_scale_notifies_the_sentence()
    {
        var action = Arm(RowActionKind.Scale);
        var changed = new List<string?>();
        action.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        action.SetCurrentScale(3, 3);

        await Assert.That(changed).Contains(nameof(RowActionViewModel.HeadlineSuffix));
        await Assert.That(changed).Contains(nameof(RowActionViewModel.Question));
    }

    [Test]
    public async Task The_sentence_names_the_object_and_the_consequence_follows_it()
    {
        var action = Arm(RowActionKind.Restart, cluster: "cluster-b");

        await Assert.That(action.Headline).IsEqualTo("Restart Deployment checkout-worker in payments on cluster-b");
        await Assert.That(action.Consequence).Contains("PodDisruptionBudgets");
        await Assert.That(action.Question).StartsWith(action.Headline);
        await Assert.That(action.Question).EndsWith(action.Consequence);
    }

    /// <summary>A cluster-scoped object has no namespace, and the sentence must not end in a dangling "in".</summary>
    [Test]
    public async Task A_cluster_scoped_object_reads_without_a_place()
    {
        var action = Arm(RowActionKind.Cordon, TestObjects.NodeDescriptor, ns: "");

        await Assert.That(action.Headline).IsEqualTo("Cordon Node checkout-worker");
    }

    [Test]
    public async Task Run_now_reads_as_run_now()
    {
        var cronJob = new ResourceDescriptor("batch", "v1", "CronJob", "cronjobs", "cronjob", true, [], []);
        var action = Arm(RowActionKind.Trigger, cronJob);

        await Assert.That(action.Headline).IsEqualTo("Run CronJob checkout-worker in payments now");
        await Assert.That(action.ConfirmLabel).IsEqualTo("Run now");
    }

    /// <summary>Only the ones that destroy something get the red glyph and the red confirm.</summary>
    [Test]
    public async Task Only_delete_drain_and_prune_are_destructive_and_every_action_has_a_glyph()
    {
        foreach (var kind in Enum.GetValues<RowActionKind>())
        {
            var action = Arm(kind);
            await Assert.That(action.IsDestructive).IsEqualTo(kind is RowActionKind.Delete or RowActionKind.Drain or RowActionKind.ArgoSyncPrune);
            await Assert.That(action.IconKey).EndsWith("IconGeometry");
            await Assert.That(action.Verb).IsNotEmpty();
            await Assert.That(action.ConfirmLabel).StartsWith(action.Verb);
        }
    }

    /// <summary>The result line leads with a moving bar while anything is in flight, a drain included.</summary>
    [Test]
    public async Task Working_covers_a_request_in_flight()
    {
        var action = Arm(RowActionKind.Restart);
        await Assert.That(action.IsWorking).IsFalse();

        action.IsBusy = true;

        await Assert.That(action.IsWorking).IsTrue();
    }
}
