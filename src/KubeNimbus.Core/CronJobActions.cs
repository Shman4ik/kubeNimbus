using System.Buffers;
using System.Text;
using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>
/// The rules behind the CronJob actions — run now, suspend and resume: which objects
/// support them, and the exact bodies they send. Pure and apart from
/// <see cref="ClusterClient"/> for the reason <see cref="WorkloadActions"/> is: a wrong
/// body here fails silently. A suspend patch one level short is a 200 that suspends
/// nothing, and a manual Job without its owner reference runs but is invisible to the
/// CronJob that is supposed to account for it. <c>CronJobActionsTests</c> pins both.
/// </summary>
public static class CronJobActions
{
    /// <summary>
    /// The annotation <c>kubectl create job --from=cronjob/…</c> stamps on the Job it
    /// creates. Written here for the same reason <see cref="WorkloadActions.RestartedAtAnnotation"/>
    /// is kubectl's own key: a manual run from kubeNimbus and one from kubectl have to be
    /// the same event to whoever reads the Job afterwards.
    /// </summary>
    public const string InstantiateAnnotation = "cronjob.kubernetes.io/instantiate";

    /// <summary>
    /// Whether this object carries a Job template (<c>spec.jobTemplate.spec.template</c>) —
    /// the object signal, as <see cref="WorkloadActions.HasPodTemplate"/> is for a restart.
    /// There is no discovery signal for "can be run now": it is a create of a Job built
    /// from this template, so the template is the evidence.
    /// </summary>
    public static bool HasJobTemplate(DynamicResource resource) =>
        resource.Raw.ValueKind == JsonValueKind.Object
        && resource.Raw.TryGetProperty("spec", out var spec)
        && spec.ValueKind == JsonValueKind.Object
        && spec.TryGetProperty("jobTemplate", out var jobTemplate)
        && jobTemplate.ValueKind == JsonValueKind.Object
        && jobTemplate.TryGetProperty("spec", out var jobSpec)
        && jobSpec.ValueKind == JsonValueKind.Object
        && jobSpec.TryGetProperty("template", out var template)
        && template.ValueKind == JsonValueKind.Object;

    /// <summary>
    /// Whether "run now" can be offered: the object has a Job template and this cluster's
    /// discovery lists a Job kind that may be created.
    /// </summary>
    /// <param name="jobDescriptor">
    /// The <c>batch</c> Job descriptor from the <em>same</em> cluster's discovery, or null
    /// when it has not been read — a run with nowhere to create the Job cannot work.
    /// </param>
    public static bool SupportsTrigger(DynamicResource resource, ResourceDescriptor? jobDescriptor) =>
        HasJobTemplate(resource) && jobDescriptor is not null && jobDescriptor.AllowsVerb("create");

    /// <summary>Whether suspend/resume can be offered: a Job template, on a kind the server says is patchable.</summary>
    public static bool SupportsSuspend(ResourceDescriptor descriptor, DynamicResource resource) =>
        HasJobTemplate(resource) && descriptor.AllowsVerb("patch");

    /// <summary>True while <c>spec.suspend</c> is true. Absent means running on schedule.</summary>
    public static bool IsSuspended(DynamicResource resource) =>
        resource.Raw.ValueKind == JsonValueKind.Object
        && resource.Raw.TryGetProperty("spec", out var spec)
        && spec.ValueKind == JsonValueKind.Object
        && spec.TryGetProperty("suspend", out var suspend)
        && suspend.ValueKind == JsonValueKind.True;

    /// <summary>
    /// The suspend/resume merge patch — <c>{"spec":{"suspend":true|false}}</c>. Resume
    /// writes an explicit <c>false</c>, as <c>kubectl patch</c> and the dashboard do, for the
    /// reason <see cref="NodeActions.CordonPatch"/> does: a null would remove the field.
    /// </summary>
    public static string SuspendPatch(bool suspend)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("spec");
            writer.WriteBoolean("suspend", suspend);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// The Job <c>kubectl create job --from=cronjob/…</c> would create: the CronJob's
    /// <c>jobTemplate.spec</c> verbatim, its template labels, its template annotations plus
    /// <see cref="InstantiateAnnotation"/>=<c>manual</c>, and a controller owner reference
    /// back to the CronJob — so the CronJob's history limits and <c>concurrencyPolicy</c>
    /// see the run, and deleting the CronJob cleans it up, exactly as for a scheduled one.
    /// </summary>
    /// <remarks>
    /// The name is left to the server through <c>generateName</c> (<c>&lt;cronjob&gt;-manual-</c>
    /// plus five characters), rather than invented here: kubectl requires one on the
    /// command line, and a client-side random suffix would be a second, weaker version of
    /// what the API server already does, including truncating a long prefix so the result
    /// still fits the 63-character label value the Job controller puts it in.
    /// </remarks>
    /// <param name="cronJob">The CronJob as just read from the server.</param>
    /// <param name="jobApiVersion">The Job kind's API version from discovery (<c>batch/v1</c>).</param>
    public static string ManualJobBody(DynamicResource cronJob, string jobApiVersion)
    {
        ArgumentNullException.ThrowIfNull(cronJob);
        if (!HasJobTemplate(cronJob))
        {
            throw new ArgumentException($"{cronJob.Name} has no spec.jobTemplate to create a Job from.", nameof(cronJob));
        }

        var raw = cronJob.Raw;
        var jobTemplate = raw.GetProperty("spec").GetProperty("jobTemplate");
        jobTemplate.TryGetProperty("metadata", out var templateMetadata);

        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("apiVersion", jobApiVersion);
            writer.WriteString("kind", "Job");

            writer.WriteStartObject("metadata");
            writer.WriteString("generateName", $"{cronJob.Name}-manual-");
            if (!string.IsNullOrEmpty(cronJob.Namespace))
            {
                writer.WriteString("namespace", cronJob.Namespace);
            }

            if (templateMetadata.ValueKind == JsonValueKind.Object
                && templateMetadata.TryGetProperty("labels", out var labels)
                && labels.ValueKind == JsonValueKind.Object)
            {
                writer.WritePropertyName("labels");
                labels.WriteTo(writer);
            }

            // kubectl stamps the instantiate annotation first and then copies the
            // template's over it, so a template that sets the key itself wins.
            writer.WriteStartObject("annotations");
            var templateAnnotations = templateMetadata.ValueKind == JsonValueKind.Object
                && templateMetadata.TryGetProperty("annotations", out var annotations)
                && annotations.ValueKind == JsonValueKind.Object
                ? annotations
                : default;
            var overridden = false;
            if (templateAnnotations.ValueKind == JsonValueKind.Object)
            {
                foreach (var annotation in templateAnnotations.EnumerateObject())
                {
                    overridden |= annotation.NameEquals(InstantiateAnnotation);
                    annotation.WriteTo(writer);
                }
            }

            if (!overridden)
            {
                writer.WriteString(InstantiateAnnotation, "manual");
            }

            writer.WriteEndObject();

            writer.WriteStartArray("ownerReferences");
            writer.WriteStartObject();
            writer.WriteString("apiVersion", raw.TryGetProperty("apiVersion", out var apiVersion)
                ? apiVersion.GetString()
                : "batch/v1");
            writer.WriteString("kind", "CronJob");
            writer.WriteString("name", cronJob.Name);
            writer.WriteString("uid", cronJob.Uid);
            writer.WriteBoolean("controller", true);
            writer.WriteBoolean("blockOwnerDeletion", true);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WritePropertyName("spec");
            jobTemplate.GetProperty("spec").WriteTo(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
