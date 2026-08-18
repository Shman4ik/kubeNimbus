using System.Text;
using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>The four operators a Kubernetes <c>matchExpressions</c> entry can carry.</summary>
public enum LabelOperator
{
    /// <summary>The label exists and its value is one of <see cref="LabelRequirement.Values"/>.</summary>
    In,

    /// <summary>The label is absent, or present with a value outside <see cref="LabelRequirement.Values"/>.</summary>
    NotIn,

    /// <summary>The label is present, whatever its value.</summary>
    Exists,

    /// <summary>The label is absent.</summary>
    DoesNotExist,
}

/// <summary>
/// One requirement of a label selector. <c>matchLabels</c> entries arrive here as
/// an <see cref="LabelOperator.In"/> over a single value, which is exactly how the
/// API server itself normalizes them — keeping one shape means
/// <see cref="LabelSelector.Matches"/> and <see cref="LabelSelector.ToQuery"/> each
/// have one code path instead of two.
/// </summary>
public sealed record LabelRequirement(string Key, LabelOperator Operator, IReadOnlyList<string> Values);

/// <summary>
/// A parsed Kubernetes label selector: what a workload's <c>spec.selector</c> says
/// about which pods belong to it. Two things are done with one of these, and both
/// matter — <see cref="ToQuery"/> renders the <c>labelSelector</c> query parameter a
/// live list or watch is issued with, and <see cref="Matches"/> answers the same
/// question locally, which is what the demo cluster (no API server at all) and the
/// tests use.
/// </summary>
/// <remarks>
/// <para>
/// Capability comes from the object, never from a list of kinds — the same discipline
/// <see cref="WorkloadActions.SupportsRestart"/> follows. A Deployment, StatefulSet,
/// DaemonSet, ReplicaSet, Job and a CRD that declares a pod selector all answer
/// <see cref="ForPodsOf"/> the same way, and none of them is named anywhere here.
/// </para>
/// <para>
/// An <b>empty</b> selector deliberately parses to null rather than to a selector that
/// matches everything. Kubernetes' own semantics for an empty <c>LabelSelector</c> are
/// "select all", and honouring that here would mean opening an aggregated log pane on
/// every pod in the namespace because an object happened to declare
/// <c>selector: {}</c> — the failure Aptakube shipped and had to withdraw
/// (aptakube#227, "Do not select all pods by default when accessing logs"). Refusing
/// is the safe direction: the action is simply not offered.
/// </para>
/// </remarks>
public sealed record LabelSelector(IReadOnlyList<LabelRequirement> Requirements)
{
    /// <summary>
    /// The selector for the pods belonging to <paramref name="workload"/>, or null when
    /// the object declares none this can use.
    /// </summary>
    /// <remarks>
    /// Both shapes Kubernetes uses for <c>spec.selector</c> are read, because both name
    /// pods: the <c>LabelSelector</c> object (<c>matchLabels</c>/<c>matchExpressions</c> —
    /// Deployment, StatefulSet, DaemonSet, ReplicaSet, Job) and the plain string map
    /// (Service, ReplicationController). They are told apart by the value shape rather
    /// than by the kind: a map's values are all strings, while <c>matchLabels</c>'s value
    /// is an object.
    /// </remarks>
    public static LabelSelector? ForPodsOf(DynamicResource workload)
    {
        if (workload.Raw.ValueKind != JsonValueKind.Object
            || !workload.Raw.TryGetProperty("spec", out var spec)
            || spec.ValueKind != JsonValueKind.Object
            || !spec.TryGetProperty("selector", out var selector)
            || selector.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return Parse(selector);
    }

    /// <summary>Parses either <c>spec.selector</c> shape; null when it carries no requirement.</summary>
    public static LabelSelector? Parse(JsonElement selector)
    {
        if (selector.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var requirements = new List<LabelRequirement>();
        var isLabelSelectorObject = false;

        if (selector.TryGetProperty("matchLabels", out var matchLabels))
        {
            isLabelSelectorObject = true;
            ReadLabelMap(matchLabels, requirements);
        }

        if (selector.TryGetProperty("matchExpressions", out var matchExpressions)
            && matchExpressions.ValueKind == JsonValueKind.Array)
        {
            isLabelSelectorObject = true;
            foreach (var expression in matchExpressions.EnumerateArray())
            {
                if (ReadExpression(expression) is { } requirement)
                {
                    requirements.Add(requirement);
                }
            }
        }

        if (!isLabelSelectorObject)
        {
            // The plain-map shape (Service, ReplicationController). Only accepted when
            // every value really is a string — anything else is a LabelSelector object
            // whose fields we do not understand, and guessing at it would produce a
            // selector that silently matches the wrong pods.
            foreach (var property in selector.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    return null;
                }
            }

            ReadLabelMap(selector, requirements);
        }

        return requirements.Count == 0 ? null : new LabelSelector(requirements);
    }

    private static void ReadLabelMap(JsonElement map, List<LabelRequirement> into)
    {
        if (map.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in map.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                into.Add(new LabelRequirement(property.Name, LabelOperator.In, [property.Value.GetString() ?? ""]));
            }
        }
    }

    private static LabelRequirement? ReadExpression(JsonElement expression)
    {
        if (expression.ValueKind != JsonValueKind.Object
            || !expression.TryGetProperty("key", out var keyElement)
            || keyElement.ValueKind != JsonValueKind.String
            || keyElement.GetString() is not { Length: > 0 } key
            || !expression.TryGetProperty("operator", out var operatorElement)
            || operatorElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var op = operatorElement.GetString() switch
        {
            "In" => LabelOperator.In,
            "NotIn" => LabelOperator.NotIn,
            "Exists" => LabelOperator.Exists,
            "DoesNotExist" => LabelOperator.DoesNotExist,

            // An operator this build does not know is not a requirement it may drop:
            // dropping it widens the selector, which is how a pane ends up tailing pods
            // the workload does not own.
            _ => (LabelOperator?)null,
        };

        if (op is not { } resolved)
        {
            return null;
        }

        var values = new List<string>();
        if (expression.TryGetProperty("values", out var valuesElement) && valuesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in valuesElement.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    values.Add(value.GetString() ?? "");
                }
            }
        }

        // In/NotIn with no values is invalid per the API's own validation, and an
        // In over nothing matches nothing — better to refuse the selector than to
        // render a query the API server rejects.
        return resolved is LabelOperator.In or LabelOperator.NotIn && values.Count == 0
            ? null
            : new LabelRequirement(key, resolved, values);
    }

    /// <summary>
    /// The <c>labelSelector</c> query value, in the syntax the API server parses
    /// (<c>k=v</c>, <c>k in (a,b)</c>, <c>k notin (a,b)</c>, <c>k</c>, <c>!k</c>),
    /// requirements comma-separated and ANDed. Not URL-escaped — the caller escapes it
    /// as one query parameter value.
    /// </summary>
    public string ToQuery()
    {
        var builder = new StringBuilder();
        foreach (var requirement in Requirements)
        {
            if (builder.Length > 0)
            {
                builder.Append(',');
            }

            switch (requirement.Operator)
            {
                case LabelOperator.In when requirement.Values.Count == 1:
                    // The equality form, which is what kubectl prints and what a
                    // matchLabels-only selector reads as everywhere else.
                    builder.Append(requirement.Key).Append('=').Append(requirement.Values[0]);
                    break;
                case LabelOperator.In:
                    builder.Append(requirement.Key).Append(" in (").AppendJoin(',', requirement.Values).Append(')');
                    break;
                case LabelOperator.NotIn:
                    builder.Append(requirement.Key).Append(" notin (").AppendJoin(',', requirement.Values).Append(')');
                    break;
                case LabelOperator.Exists:
                    builder.Append(requirement.Key);
                    break;
                case LabelOperator.DoesNotExist:
                    builder.Append('!').Append(requirement.Key);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Whether an object carrying <paramref name="labels"/> is selected. Mirrors the
    /// API server's own semantics, including the one that surprises people:
    /// <c>notin</c> and <c>DoesNotExist</c> both match an object that has no such label
    /// at all.
    /// </summary>
    public bool Matches(IReadOnlyDictionary<string, string> labels)
    {
        foreach (var requirement in Requirements)
        {
            var present = labels.TryGetValue(requirement.Key, out var value);
            var satisfied = requirement.Operator switch
            {
                LabelOperator.In => present && requirement.Values.Contains(value!, StringComparer.Ordinal),
                LabelOperator.NotIn => !present || !requirement.Values.Contains(value!, StringComparer.Ordinal),
                LabelOperator.Exists => present,
                LabelOperator.DoesNotExist => !present,
                _ => false,
            };

            if (!satisfied)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Human-readable form for a pane header — the same text <see cref="ToQuery"/> produces.</summary>
    public override string ToString() => ToQuery();
}
