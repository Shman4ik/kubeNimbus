using System.Globalization;
using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>
/// One column a CustomResourceDefinition declares for itself
/// (<c>spec.versions[].additionalPrinterColumns[]</c>) — the CRD author's own answer
/// to "what are the three things you want to see about this object in a list".
/// </summary>
/// <param name="Name">The column header, verbatim from the CRD.</param>
/// <param name="Type">OpenAPI type: <c>integer</c>, <c>number</c>, <c>string</c>,
/// <c>boolean</c> or <c>date</c>. Decides how the extracted value is rendered — a
/// <c>date</c> becomes an age, which is what the API server does before kubectl ever
/// sees the cell.</param>
/// <param name="JsonPath">The (simple) JSONPath into each object — see
/// <see cref="SimpleJsonPath"/>.</param>
/// <param name="Priority">0 shows in the default table; anything higher is
/// <c>kubectl get -o wide</c> only. Unset means 0.</param>
/// <param name="Description">The CRD's own prose for the column, used as the header
/// tooltip. Frequently empty.</param>
public sealed record PrinterColumn(
    string Name,
    string Type,
    string JsonPath,
    int Priority = 0,
    string Description = "");

/// <summary>
/// Reading a CRD's printer columns, and rendering one object's cell for each.
///
/// <para>
/// The pair of them is what makes a CRD list look like <c>kubectl get &lt;crd&gt;</c>
/// instead of like a generic object table. Built-in kinds never come through here —
/// they are not CustomResourceDefinitions, so there is nothing to read, and
/// <c>ResourceStatusSummary</c> keeps owning them exactly as before.
/// </para>
///
/// <para>
/// Pure functions over <see cref="JsonElement"/>, deliberately: the whole behaviour is
/// decided by the CRD document and the object in front of it, so it can be exercised
/// from two fixture strings with no client and no cluster.
/// </para>
/// </summary>
public static class PrinterColumns
{
    /// <summary>
    /// The columns <paramref name="crd"/> declares for <paramref name="version"/> — a
    /// whole <c>CustomResourceDefinition</c> object as the API server serves it.
    ///
    /// <para>
    /// Empty when the CRD says nothing for that version, and that is the same answer
    /// the API server gives: <c>serveDefaultColumnsIfEmpty</c> substitutes a lone Age
    /// column, which this app already draws from <c>metadata.creationTimestamp</c> on
    /// every row of every kind. So "no printer columns" degrades exactly to today's
    /// list, which is the required behaviour, not a fallback bolted on.
    /// </para>
    /// </summary>
    public static IReadOnlyList<PrinterColumn> Parse(JsonElement crd, string version)
    {
        if (crd.ValueKind != JsonValueKind.Object
            || !crd.TryGetProperty("spec", out var spec)
            || !spec.TryGetProperty("versions", out var versions)
            || versions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (var entry in versions.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("name", out var name)
                || !string.Equals(name.GetString(), version, StringComparison.Ordinal))
            {
                continue;
            }

            return ParseColumns(entry);
        }

        return [];
    }

    private static IReadOnlyList<PrinterColumn> ParseColumns(JsonElement version)
    {
        if (!version.TryGetProperty("additionalPrinterColumns", out var columns)
            || columns.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<PrinterColumn>();
        foreach (var column in columns.EnumerateArray())
        {
            if (column.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = Str(column, "name");
            var jsonPath = Str(column, "jsonPath");
            if (name.Length == 0 || jsonPath.Length == 0)
            {
                continue; // both are required by the CRD schema; a malformed one is skipped, never thrown on
            }

            result.Add(new PrinterColumn(
                Name: name,
                Type: Str(column, "type"),
                JsonPath: jsonPath,
                Priority: column.TryGetProperty("priority", out var p) && p.ValueKind == JsonValueKind.Number
                    && p.TryGetInt32(out var priority)
                    ? priority
                    : 0,
                Description: Str(column, "description")));
        }

        return result;
    }

    /// <summary>
    /// The subset of <paramref name="all"/> the list actually draws.
    ///
    /// <para>
    /// Three rules, and each one is a decision worth not re-deriving:
    /// </para>
    /// <list type="number">
    /// <item><b>Priority is kubectl's own <c>-o wide</c> lever</b>, so it is wired to
    /// the advanced view. A CRD author already marked the columns that matter less by
    /// giving them <c>priority: 1</c>, and the app already has one switch whose whole
    /// job is "show me the busier layout". Ignoring priority would mean the eleven
    /// columns KEDA declares for a ScaledObject all arrive at once in a list that UI
    /// rule 14 says is already tight at 1280px.</item>
    /// <item><b>A declared Age over <c>.metadata.creationTimestamp</c> is dropped</b>,
    /// because the list's own Age column is that column — computed live off the shared
    /// clock and carrying the exact timestamp as a tooltip, which a printer cell
    /// re-evaluated only on a watch event is not. A column named Age pointing anywhere
    /// else is kept: that one is not ours.</item>
    /// <item><b>The count is capped</b> at <paramref name="max"/>, because the grid's
    /// printer slots are declared in XAML (a DataGridColumn is outside the visual tree
    /// and cannot be bound, and generating them in code would mean reflection
    /// bindings, which this repo does not ship). The cap is above every real CRD found
    /// while building this — KEDA's ScaledObject, the widest, needs ten before the Age
    /// rule and nine after.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<PrinterColumn> Visible(
        IReadOnlyList<PrinterColumn> all, bool includeLowPriority, int max)
    {
        if (all.Count == 0 || max <= 0)
        {
            return [];
        }

        var result = new List<PrinterColumn>();
        foreach (var column in all)
        {
            if (!includeLowPriority && column.Priority > 0)
            {
                continue;
            }

            if (IsRedundantAge(column))
            {
                continue;
            }

            result.Add(column);
            if (result.Count == max)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// The column the list already draws for every object of every kind: an Age over
    /// the object's own creation timestamp. Matched on the path as well as the name so
    /// a CRD that calls something else "Age" keeps it.
    /// </summary>
    private static bool IsRedundantAge(PrinterColumn column) =>
        string.Equals(column.Name, "Age", StringComparison.OrdinalIgnoreCase)
        && column.JsonPath.TrimStart('.') is "metadata.creationTimestamp";

    /// <summary>
    /// One cell: the value at the column's path, rendered per the column's declared
    /// type. Mirrors the API server's <c>tableconvertor.cellForJSONValue</c>, which is
    /// what produces the string kubectl prints — including its two sentinels
    /// (<c>&lt;unknown&gt;</c> for a zero timestamp, <c>&lt;invalid&gt;</c> for one it
    /// cannot parse), since a user comparing the two screens should not have to
    /// wonder which of them is guessing.
    ///
    /// <para>
    /// An absent field, an unresolvable path and a non-scalar value all render as an
    /// empty cell. The API server emits a null cell for all three and kubectl prints
    /// nothing for a null; an empty cell in a grid says the same thing without
    /// inventing a word for it.
    /// </para>
    ///
    /// <para>
    /// <paramref name="now"/> is only ever passed explicitly by tests and by the
    /// screenshot harness, which need a stable clock; the app passes none.
    /// </para>
    /// </summary>
    public static string Evaluate(PrinterColumn column, JsonElement resource, DateTimeOffset? now = null)
    {
        if (!SimpleJsonPath.TryEvaluate(resource, column.JsonPath, out var value))
        {
            return "";
        }

        // The API server skips object/array values outright rather than dumping JSON
        // into a table cell, and so does this.
        var text = SimpleJsonPath.ScalarText(value);
        if (text is null)
        {
            return "";
        }

        // integer / number / boolean / string all render as the scalar's own text —
        // JSON already carries the distinction, and re-formatting a number would only
        // introduce a way for the app to disagree with the object. Only `date` is a
        // transformation, and it is the API server's, not ours.
        return column.Type == "date" ? FormatDate(text, now ?? DateTimeOffset.UtcNow) : text;
    }

    /// <summary>
    /// The timestamp a <c>date</c> column points at, if it is one — so the list can
    /// re-render that cell off its shared age timer instead of leaving "3m" on screen
    /// for as long as nothing else about the object changes.
    /// </summary>
    public static DateTimeOffset? DateValue(PrinterColumn column, JsonElement resource)
    {
        if (column.Type != "date"
            || !SimpleJsonPath.TryEvaluate(resource, column.JsonPath, out var value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { Length: > 0 } text
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return null;
        }

        return parsed;
    }

    private static string FormatDate(string text, DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return "<invalid>";
        }

        // metav1.Time's zero value round-trips as "0001-01-01T00:00:00Z"; the API
        // server prints <unknown> for it rather than an age of two thousand years.
        return parsed.UtcDateTime == DateTime.MinValue ? "<unknown>" : RelativeTime.Compact(now - parsed);
    }

    private static string Str(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
