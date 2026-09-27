using System.Text.Json.Nodes;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// FEAT-8 in the App layer: which of run-now / suspend / resume the selected row offers,
/// that each arms the shared confirm strip (UI rule 17) and refuses in place on the demo
/// cluster, that the menu follows a CronJob whose suspend flag the watch has just changed,
/// and that a Job opens in the workload pane where its pods are.
/// </summary>
public class CronJobActionTests
{
    private static readonly ResourceDescriptor JobDescriptor =
        new("batch", "v1", "Job", "jobs", "job", Namespaced: true, ShortNames: [], Categories: []);

    private static readonly ResourceDescriptor CronJobDescriptor =
        new("batch", "v1", "CronJob", "cronjobs", "cronjob", Namespaced: true, ShortNames: [], Categories: []);

    private static ClusterTabViewModel DemoKind(string group, string kind)
    {
        TestObjects.RedirectStores();
        var tab = new ClusterTabViewModel(ClusterContext.Demo);
        tab.ConnectCommand.Execute(null);
        tab.SelectKindCommand.Execute(tab.SidebarSections
            .SelectMany(s => s.Kinds)
            .First(k => k.Descriptor.Group == group && k.Descriptor.Kind == kind));
        return tab;
    }

    private static ResourceRowViewModel Row(ClusterTabViewModel tab, string name) => tab.Rows.First(r => r.Name == name);

    [Test]
    public async Task A_scheduled_cronjob_offers_run_now_and_suspend()
    {
        var tab = DemoKind("batch", "CronJob");
        tab.SelectedRow = Row(tab, "nightly-reconcile");

        await Assert.That(tab.CanTriggerSelectedRow).IsTrue();
        await Assert.That(tab.CanSuspendSelectedRow).IsTrue();
        await Assert.That(tab.CanResumeSelectedRow).IsFalse();
    }

    [Test]
    public async Task A_suspended_cronjob_offers_resume_instead_of_suspend()
    {
        var tab = DemoKind("batch", "CronJob");
        tab.SelectedRow = Row(tab, "quarterly-report");

        await Assert.That(tab.CanTriggerSelectedRow).IsTrue();
        await Assert.That(tab.CanSuspendSelectedRow).IsFalse();
        await Assert.That(tab.CanResumeSelectedRow).IsTrue();
    }

    [Test]
    public async Task Nothing_cronjob_shaped_is_offered_on_a_deployment()
    {
        var tab = DemoKind("apps", "Deployment");
        tab.SelectedRow = tab.Rows.First();

        await Assert.That(tab.CanTriggerSelectedRow).IsFalse();
        await Assert.That(tab.CanSuspendSelectedRow).IsFalse();
        await Assert.That(tab.CanResumeSelectedRow).IsFalse();
    }

    /// <summary>A Job's pod template is immutable: the list must not offer a rollout restart on one.</summary>
    [Test]
    public async Task A_job_row_is_not_offered_a_rollout_restart()
    {
        var tab = DemoKind("batch", "Job");
        tab.SelectedRow = tab.Rows.First();

        await Assert.That(tab.CanRestartSelectedRow).IsFalse();
    }

    /// <summary>
    /// The watch changes the selected CronJob in place when a suspend goes through, and the
    /// menu has to follow it — a strip that says "suspended" above a menu still offering
    /// Suspend is two answers to one question.
    /// </summary>
    [Test]
    public async Task The_menu_follows_a_suspend_the_watch_reports_on_the_selected_row()
    {
        var tab = DemoKind("batch", "CronJob");
        var row = Row(tab, "nightly-reconcile");
        tab.SelectedRow = row;
        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var node = JsonNode.Parse(row.Resource.Raw.GetRawText())!;
        node["spec"]!["suspend"] = true;
        using var document = System.Text.Json.JsonDocument.Parse(node.ToJsonString());
        tab.Apply(TestObjects.Modified(new DynamicResource(document.RootElement.Clone())));

        await Assert.That(tab.CanSuspendSelectedRow).IsFalse();
        await Assert.That(tab.CanResumeSelectedRow).IsTrue();
        await Assert.That(raised).Contains(nameof(ClusterTabViewModel.CanResumeSelectedRow));
        await Assert.That(tab.ResumeSelectedCommand.CanExecute(null)).IsTrue();
    }

    [Test]
    public async Task Run_now_arms_the_strip_and_refuses_in_place_on_the_demo_cluster()
    {
        var tab = DemoKind("batch", "CronJob");
        tab.SelectedRow = Row(tab, "nightly-reconcile");

        tab.TriggerSelectedCommand.Execute(null);

        var action = tab.PendingRowAction!;
        await Assert.That(action.Kind).IsEqualTo(RowActionKind.Trigger);
        await Assert.That(action.Target).Contains("CronJob/nightly-reconcile");
        await Assert.That(action.ConfirmLabel).IsEqualTo("Run now");
        await Assert.That(action.IsDemo).IsTrue();
        await Assert.That(action.ConfirmCommand.CanExecute(null)).IsFalse();
    }

    [Test]
    public async Task Resume_says_a_missed_run_can_start_straight_away()
    {
        var action = new RowActionViewModel(RowActionKind.Resume, client: null, CronJobDescriptor, "payments", "quarterly-report");

        await Assert.That(action.Question).Contains("missed while suspended");
        await Assert.That(action.ConfirmLabel).IsEqualTo("Resume");
    }

    /// <summary>A run-now with no Job kind to create through is a button that cannot work, so it is dead.</summary>
    [Test]
    public async Task Run_now_cannot_confirm_without_the_clusters_job_kind()
    {
        var client = TestObjects.OfflineClient();
        var without = new RowActionViewModel(RowActionKind.Trigger, client, CronJobDescriptor, "payments", "nightly");
        var with = new RowActionViewModel(
            RowActionKind.Trigger, client, CronJobDescriptor, "payments", "nightly", jobDescriptor: JobDescriptor);

        await Assert.That(without.ConfirmCommand.CanExecute(null)).IsFalse();
        await Assert.That(with.ConfirmCommand.CanExecute(null)).IsTrue();
    }

    /// <summary>
    /// "Open Job" is offered only once the server has named the Job, and it opens what the
    /// cluster tab hands it — the created Job's detail.
    /// </summary>
    [Test]
    public async Task Open_job_is_offered_once_a_job_was_created_and_opens_it()
    {
        DynamicResource? opened = null;
        var action = new RowActionViewModel(
            RowActionKind.Trigger, TestObjects.OfflineClient(), CronJobDescriptor, "payments", "nightly", jobDescriptor: JobDescriptor)
        {
            OpenJob = job => { opened = job; return Task.CompletedTask; },
        };
        await Assert.That(action.HasFollowUp).IsFalse();

        var job = TestObjects.Pod("payments", "nightly-manual-x7k2m");
        action.CreatedJob = job;
        await Assert.That(action.HasFollowUp).IsTrue();

        await action.OpenCreatedJobCommand.ExecuteAsync(null);
        await Assert.That(opened).IsSameReferenceAs(job);
    }

    /// <summary>A Job opens in the workload pane, which is where its pods are — the item's other half.</summary>
    [Test]
    public async Task A_job_opens_in_the_workload_pane_with_its_run_progress()
    {
        var tab = DemoKind("batch", "Job");
        tab.SelectedRow = Row(tab, "nightly-reconcile-29230920");

        await tab.OpenSelectedCommand.ExecuteAsync(null);

        var detail = tab.SelectedInspectorTab as WorkloadDetailTabViewModel;
        await Assert.That(detail).IsNotNull();
        await Assert.That(detail!.Title).IsEqualTo("Job/nightly-reconcile-29230920");
        await Assert.That(detail.Rollout).IsEqualTo("0/1 succeeded · 0 running · 4 failed (backoff limit 3)");
        await Assert.That(detail.Conditions.Select(c => c.Type)).Contains("Failed");
        await Assert.That(detail.CanRestart).IsFalse();
    }
}
