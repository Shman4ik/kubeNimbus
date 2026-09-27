using System.Net;
using System.Text.Json;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// FEAT-8: run a CronJob now, suspend it, resume it. The bodies are pinned because each
/// fails silently when wrong — a Job with no owner reference runs and is then invisible to
/// the CronJob's history limits and concurrency policy, and a suspend patch a level short
/// is a 200 that changes nothing.
/// </summary>
public class CronJobActionsTests
{
    private static DynamicResource Parse(string json) => new(JsonDocument.Parse(json).RootElement.Clone());

    private static readonly ResourceDescriptor CronJobs =
        new("batch", "v1", "CronJob", "cronjobs", "cronjob", Namespaced: true, ShortNames: ["cj"], Categories: []);

    private static readonly ResourceDescriptor Jobs =
        new("batch", "v1", "Job", "jobs", "job", Namespaced: true, ShortNames: [], Categories: []);

    private const string CronJobJson = """
        {
          "apiVersion": "batch/v1",
          "kind": "CronJob",
          "metadata": { "name": "nightly-reconcile", "namespace": "payments", "uid": "cj-uid" },
          "spec": {
            "schedule": "0 2 * * *",
            "jobTemplate": {
              "metadata": { "labels": { "app": "reconcile" }, "annotations": { "team": "ledger" } },
              "spec": {
                "backoffLimit": 3,
                "template": { "spec": { "restartPolicy": "Never", "containers": [ { "name": "run", "image": "r:1" } ] } }
              }
            }
          }
        }
        """;

    [Test]
    public async Task A_manual_job_is_the_job_template_owned_by_the_cronjob_and_marked_manual()
    {
        var body = CronJobActions.ManualJobBody(Parse(CronJobJson), "batch/v1");

        await Assert.That(body).IsEqualTo(
            """{"apiVersion":"batch/v1","kind":"Job","metadata":{"generateName":"nightly-reconcile-manual-","namespace":"payments","labels":{"app":"reconcile"},"annotations":{"team":"ledger","cronjob.kubernetes.io/instantiate":"manual"},"ownerReferences":[{"apiVersion":"batch/v1","kind":"CronJob","name":"nightly-reconcile","uid":"cj-uid","controller":true,"blockOwnerDeletion":true}]},"spec":{"backoffLimit":3,"template":{"spec":{"restartPolicy":"Never","containers":[{"name":"run","image":"r:1"}]}}}}""");
    }

    /// <summary>kubectl copies the template's annotations over its own, so a template that sets the key wins.</summary>
    [Test]
    public async Task A_template_that_sets_the_instantiate_annotation_is_not_given_a_second_one()
    {
        var cronJob = Parse(CronJobJson.Replace("\"team\": \"ledger\"", "\"cronjob.kubernetes.io/instantiate\": \"custom\""));

        using var doc = JsonDocument.Parse(CronJobActions.ManualJobBody(cronJob, "batch/v1"));
        var annotations = doc.RootElement.GetProperty("metadata").GetProperty("annotations");

        await Assert.That(annotations.EnumerateObject().Count()).IsEqualTo(1);
        await Assert.That(annotations.GetProperty(CronJobActions.InstantiateAnnotation).GetString()).IsEqualTo("custom");
    }

    [Test]
    [Arguments(true, """{"spec":{"suspend":true}}""")]
    [Arguments(false, """{"spec":{"suspend":false}}""")]
    public async Task Suspend_and_resume_patch_spec_suspend_and_nothing_else(bool suspend, string expected)
    {
        await Assert.That(CronJobActions.SuspendPatch(suspend)).IsEqualTo(expected);
    }

    [Test]
    public async Task Run_now_needs_a_job_template_and_a_creatable_job_kind()
    {
        var cronJob = Parse(CronJobJson);

        await Assert.That(CronJobActions.SupportsTrigger(cronJob, Jobs)).IsTrue();
        await Assert.That(CronJobActions.SupportsTrigger(cronJob, jobDescriptor: null)).IsFalse();
        await Assert.That(CronJobActions.SupportsTrigger(cronJob, Jobs with { Verbs = ["get", "list"] })).IsFalse();

        // A Deployment has a pod template and no Job template: nothing to run.
        var deployment = Parse("""{"kind":"Deployment","metadata":{"name":"d"},"spec":{"template":{"spec":{"containers":[]}}}}""");
        await Assert.That(CronJobActions.SupportsTrigger(deployment, Jobs)).IsFalse();
        await Assert.That(CronJobActions.SupportsSuspend(CronJobs, deployment)).IsFalse();
    }

    [Test]
    public async Task Suspended_reads_spec_suspend_and_absent_means_running()
    {
        await Assert.That(CronJobActions.IsSuspended(Parse(CronJobJson))).IsFalse();
        await Assert.That(CronJobActions.IsSuspended(Parse(CronJobJson.Replace("\"schedule\"", "\"suspend\": true, \"schedule\"")))).IsTrue();
    }

    /// <summary>
    /// A Job carries a pod template too, but the API server holds it immutable: a restart
    /// stamp is a 422, and nothing would roll. It must not be offered.
    /// </summary>
    [Test]
    public async Task A_job_is_not_offered_a_rollout_restart()
    {
        var job = Parse("""{"kind":"Job","metadata":{"name":"j"},"spec":{"template":{"spec":{"containers":[{"name":"a"}]}}}}""");

        await Assert.That(WorkloadActions.SupportsRestart(Jobs, job)).IsFalse();
        await Assert.That(WorkloadActions.HasPodTemplate(job)).IsTrue();
    }

    /// <summary>
    /// Over HTTP: the CronJob is read at the moment of the run (so an edit since the list
    /// last ticked is what runs), and the Job is posted to the Job collection of the
    /// CronJob's namespace. The name in the answer is the server's.
    /// </summary>
    [Test]
    public async Task Running_now_reads_the_cronjob_and_posts_the_job_to_its_namespace()
    {
        using var server = new ApplyPreviewHttpTests.StubApiServer();
        server.Respond("GET", "/apis/batch/v1/namespaces/payments/cronjobs/nightly-reconcile", HttpStatusCode.OK, CronJobJson);
        server.Respond("POST", "/apis/batch/v1/namespaces/payments/jobs", HttpStatusCode.Created,
            """{"apiVersion":"batch/v1","kind":"Job","metadata":{"name":"nightly-reconcile-manual-x7k2m","namespace":"payments"}}""");
        using var client = server.Connect();

        var job = await client.CreateJobFromCronJobAsync(CronJobs, Jobs, "payments", "nightly-reconcile");

        await Assert.That(job.Name).IsEqualTo("nightly-reconcile-manual-x7k2m");
        var post = server.Requests.Single(r => r.Method == "POST");
        await Assert.That(post.ContentType).StartsWith("application/json");
        using var body = JsonDocument.Parse(post.Body);
        await Assert.That(body.RootElement.GetProperty("metadata").GetProperty("ownerReferences")[0]
            .GetProperty("uid").GetString()).IsEqualTo("cj-uid");
    }

    [Test]
    public async Task Suspending_patches_the_cronjob_with_a_merge_patch()
    {
        using var server = new ApplyPreviewHttpTests.StubApiServer();
        server.Respond("PATCH", "/apis/batch/v1/namespaces/payments/cronjobs/nightly-reconcile", HttpStatusCode.OK, CronJobJson);
        using var client = server.Connect();

        await client.SetCronJobSuspendedAsync(CronJobs, "payments", "nightly-reconcile", suspended: true);

        var patch = server.Requests.Single();
        await Assert.That(patch.ContentType).StartsWith("application/merge-patch+json");
        await Assert.That(patch.Body).IsEqualTo("""{"spec":{"suspend":true}}""");
    }
}
