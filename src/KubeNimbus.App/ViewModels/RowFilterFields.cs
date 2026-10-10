using System.Text.Json;
using KubeNimbus.Core;
using KubeNimbus.Core.Networking;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// What the list search (UI rule 13) matches, kind by kind — the one place the rule is
/// written as code. The detail and the reasons are in
/// <c>docs/engineering/list-search.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule:</b> a row matches on the fields that <em>identify</em> its object, never on
/// a status. Every kind contributes its name and namespace, and its cluster in a fleet list.
/// A few kinds add the fields that identify them to the person looking for one:
/// </para>
/// <list type="table">
/// <item><term>Event (core and <c>events.k8s.io</c>)</term><description>reason, object, message — its own
/// name is a generated <c>&lt;object&gt;.&lt;hex&gt;</c> nobody types</description></item>
/// <item><term>Ingress (<c>networking.k8s.io</c>)</term><description>every <c>spec.rules[].host</c>
/// — the hostname is how an Ingress is known (FEAT-65)</description></item>
/// </list>
/// <para>
/// Never a status or a condition (an Event's Type, a pod's phase, a CRD's Ready column),
/// because "Running" or "True" would match most of a healthy list; and never a kind's
/// printer cells wholesale, because what the box matches would then change from kind to
/// kind with no rule a reader could learn. A new row in the table is a field that names the
/// object, written here and on the engineering page in the same change.
/// </para>
/// </remarks>
internal static class RowFilterFields
{
    /// <summary>
    /// The identity fields a kind adds beyond name, namespace and cluster, read from the
    /// object when its row is built or updated. Events are not here: their three fields are
    /// the row's own cells, matched in <see cref="Matches"/>.
    /// </summary>
    public static IReadOnlyList<string> KindFields(DynamicResource resource)
    {
        var apiVersion = resource.ApiVersion;
        var slash = apiVersion.IndexOf('/');
        var group = slash < 0 ? "" : apiVersion[..slash];
        return (group, resource.Kind) switch
        {
            ("networking.k8s.io", "Ingress") when resource.Raw.TryGetProperty("spec", out var spec)
                && spec.ValueKind == JsonValueKind.Object => IngressRules.RuleHosts(spec),
            _ => [],
        };
    }

    /// <summary>Case-insensitive substring match of <paramref name="query"/> against the row's identity fields.</summary>
    public static bool Matches(ResourceRowViewModel row, string query) =>
        Contains(row.Name, query)
        || Contains(row.Namespace, query)
        || (row.ClusterName.Length > 0 && Contains(row.ClusterName, query))
        || (row.IsEvent
            && (Contains(row.EventReason, query)
                || Contains(row.EventObject, query)
                || Contains(row.EventMessageTooltip, query)))
        || row.FilterFields.Any(field => Contains(field, query));

    private static bool Contains(string field, string query) =>
        field.Contains(query, StringComparison.OrdinalIgnoreCase);
}
