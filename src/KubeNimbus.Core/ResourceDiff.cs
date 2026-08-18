using System.Text;
using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>What happened to one field between the live object and the server's dry-run result.</summary>
public enum ResourceChangeKind
{
    Added,
    Removed,
    Changed,
}

/// <summary>
/// One line of a diff: the field's path, what it was, and what it would become.
/// Values are already rendered for display — a diff is read, not re-parsed, and
/// keeping <see cref="JsonElement"/>s here would tie every row to the lifetime of
/// the document it came from.
/// </summary>
public sealed record ResourceChange(string Path, ResourceChangeKind Kind, string? Before, string? After);

/// <summary>
/// The difference between an object as it is on the server and as the server says
/// it would be after an apply. Both sides come from the API server — the live GET
/// and the <c>dryRun=All</c> response — never from the editor's text, which is the
/// whole point: what shows up here includes admission webhooks, defaulting and
/// controller-owned fields, none of which a local diff of the manifest can know
/// about.
/// </summary>
/// <remarks>
/// <para>
/// Three fields are excluded and counted instead of shown. <c>metadata.managedFields</c>
/// is the apply's own bookkeeping and changes on every apply, including one that
/// changes nothing else; <c>metadata.resourceVersion</c> and <c>metadata.generation</c>
/// are the server's counters. Leaving them in is what makes <c>kubectl diff</c>'s
/// output hard to read, and none of the three tells anyone anything about the change
/// they are about to make.
/// </para>
/// <para>
/// Lists are matched by <c>name</c> when every element on both sides is an object
/// with a unique <c>name</c> — containers, ports, env vars, volumes and volume
/// mounts all have that shape, and it is Kubernetes' own merge key for them.
/// Without it, inserting one container at the front reports every container as
/// changed. A pure reordering is still reported, as one line naming the two
/// sequences, because for <c>env</c> the order is semantic.
/// </para>
/// </remarks>
public sealed class ResourceDiff
{
    /// <summary>
    /// How many changes are kept. A diff this long is not being read line by line
    /// any more, and every row is a rendered string held for as long as the panel
    /// is open. The count of what was dropped is reported rather than the rows.
    /// </summary>
    public const int MaxChanges = 200;

    /// <summary>How much of one value is rendered before it is elided.</summary>
    private const int MaxValueChars = 240;

    private static readonly string[] BookkeepingPaths =
    [
        "metadata.managedFields",
        "metadata.resourceVersion",
        "metadata.generation",
    ];

    private ResourceDiff(IReadOnlyList<ResourceChange> changes, int totalChanges, int hiddenBookkeepingCount, bool isCreate)
    {
        Changes = changes;
        TotalChanges = totalChanges;
        HiddenBookkeepingCount = hiddenBookkeepingCount;
        IsCreate = isCreate;
    }

    /// <summary>The rendered changes, capped at <see cref="MaxChanges"/>.</summary>
    public IReadOnlyList<ResourceChange> Changes { get; }

    /// <summary>How many changes there were before the cap — equal to <c>Changes.Count</c> when nothing was dropped.</summary>
    public int TotalChanges { get; }

    /// <summary>How many server-bookkeeping fields were excluded. Reported so their absence is a decision, not a gap.</summary>
    public int HiddenBookkeepingCount { get; }

    /// <summary>True when the object does not exist yet, so the apply would create it.</summary>
    public bool IsCreate { get; }

    public bool IsTruncated => TotalChanges > Changes.Count;

    /// <summary>
    /// True when the server says the apply would change nothing. Worth its own state:
    /// it is the answer to "did my edit actually do anything", and it is also what a
    /// re-apply of an unchanged document looks like.
    /// </summary>
    public bool IsEmpty => Changes.Count == 0;

    /// <summary>
    /// Diffs the live object against the server's dry-run result. A null
    /// <paramref name="live"/> means the object does not exist yet; the result is
    /// then the new object's own top-level fields rather than one entry for the
    /// whole document.
    /// </summary>
    public static ResourceDiff Between(JsonElement? live, JsonElement previewed)
    {
        var changes = new List<ResourceChange>();
        var hidden = 0;
        var isCreate = live is null;

        using var empty = isCreate ? JsonDocument.Parse("{}") : null;
        var before = live ?? empty!.RootElement;

        Compare(path: "", before, previewed, changes, ref hidden);

        var total = changes.Count;
        return new ResourceDiff(
            total > MaxChanges ? changes.GetRange(0, MaxChanges) : changes,
            total,
            hidden,
            isCreate);
    }

    private static void Compare(string path, JsonElement before, JsonElement after, List<ResourceChange> sink, ref int hidden)
    {
        if (IsBookkeeping(path))
        {
            if (!SameJson(before, after))
            {
                hidden++;
            }

            return;
        }

        if (before.ValueKind != after.ValueKind)
        {
            Add(sink, path, ResourceChangeKind.Changed, Render(before), Render(after));
            return;
        }

        switch (before.ValueKind)
        {
            case JsonValueKind.Object:
                CompareObjects(path, before, after, sink, ref hidden);
                return;
            case JsonValueKind.Array:
                CompareArrays(path, before, after, sink, ref hidden);
                return;
            default:
                if (!SameScalar(before, after))
                {
                    Add(sink, path, ResourceChangeKind.Changed, Render(before), Render(after));
                }

                return;
        }
    }

    private static void CompareObjects(string path, JsonElement before, JsonElement after, List<ResourceChange> sink, ref int hidden)
    {
        // The new object's own property order leads, so the diff reads in the order the
        // document is written; properties that only exist on the old side follow.
        foreach (var property in after.EnumerateObject())
        {
            var child = Join(path, property.Name);
            if (before.TryGetProperty(property.Name, out var oldValue))
            {
                Compare(child, oldValue, property.Value, sink, ref hidden);
            }
            else if (IsBookkeeping(child))
            {
                hidden++;
            }
            else
            {
                Add(sink, child, ResourceChangeKind.Added, before: null, Render(property.Value));
            }
        }

        foreach (var property in before.EnumerateObject())
        {
            if (after.TryGetProperty(property.Name, out _))
            {
                continue;
            }

            var child = Join(path, property.Name);
            if (IsBookkeeping(child))
            {
                hidden++;
            }
            else
            {
                Add(sink, child, ResourceChangeKind.Removed, Render(property.Value), after: null);
            }
        }
    }

    private static void CompareArrays(string path, JsonElement before, JsonElement after, List<ResourceChange> sink, ref int hidden)
    {
        if (TryNameKeys(before, out var beforeNames) && TryNameKeys(after, out var afterNames))
        {
            CompareByName(path, before, beforeNames, after, afterNames, sink, ref hidden);
            return;
        }

        var beforeItems = before.EnumerateArray().ToArray();
        var afterItems = after.EnumerateArray().ToArray();
        var shared = Math.Min(beforeItems.Length, afterItems.Length);

        for (var i = 0; i < shared; i++)
        {
            Compare($"{path}[{i}]", beforeItems[i], afterItems[i], sink, ref hidden);
        }

        for (var i = shared; i < afterItems.Length; i++)
        {
            Add(sink, $"{path}[{i}]", ResourceChangeKind.Added, before: null, Render(afterItems[i]));
        }

        for (var i = shared; i < beforeItems.Length; i++)
        {
            Add(sink, $"{path}[{i}]", ResourceChangeKind.Removed, Render(beforeItems[i]), after: null);
        }
    }

    private static void CompareByName(
        string path,
        JsonElement before,
        IReadOnlyList<string> beforeNames,
        JsonElement after,
        IReadOnlyList<string> afterNames,
        List<ResourceChange> sink,
        ref int hidden)
    {
        var beforeItems = before.EnumerateArray().ToArray();
        var afterItems = after.EnumerateArray().ToArray();
        var beforeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < beforeNames.Count; i++)
        {
            beforeIndex[beforeNames[i]] = i;
        }

        for (var i = 0; i < afterNames.Count; i++)
        {
            var name = afterNames[i];
            if (beforeIndex.TryGetValue(name, out var j))
            {
                Compare($"{path}[{name}]", beforeItems[j], afterItems[i], sink, ref hidden);
            }
            else
            {
                Add(sink, $"{path}[{name}]", ResourceChangeKind.Added, before: null, Render(afterItems[i]));
            }
        }

        var afterSet = new HashSet<string>(afterNames, StringComparer.Ordinal);
        for (var i = 0; i < beforeNames.Count; i++)
        {
            if (!afterSet.Contains(beforeNames[i]))
            {
                Add(sink, $"{path}[{beforeNames[i]}]", ResourceChangeKind.Removed, Render(beforeItems[i]), after: null);
            }
        }

        // A pure reordering changes no element and would otherwise be invisible. It is
        // semantic for env (later entries can expand earlier ones) and for initContainers,
        // so it gets its own line rather than being folded into the elements above.
        var commonBefore = beforeNames.Where(afterSet.Contains).ToArray();
        var commonAfter = afterNames.Where(beforeIndex.ContainsKey).ToArray();
        if (!commonBefore.SequenceEqual(commonAfter, StringComparer.Ordinal))
        {
            Add(
                sink,
                $"{path} (order)",
                ResourceChangeKind.Changed,
                string.Join(", ", commonBefore),
                string.Join(", ", commonAfter));
        }
    }

    /// <summary>
    /// The list's merge key, when it has one: every element an object carrying a
    /// non-empty string <c>name</c>, all of them distinct. Anything else — a list of
    /// scalars, a list of objects without names, or two entries sharing a name — falls
    /// back to index comparison, because a wrong pairing invents changes.
    /// </summary>
    private static bool TryNameKeys(JsonElement array, out IReadOnlyList<string> names)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || name.GetString() is not { Length: > 0 } text
                || !seen.Add(text))
            {
                names = [];
                return false;
            }

            result.Add(text);
        }

        names = result;
        return result.Count > 0;
    }

    private static void Add(List<ResourceChange> sink, string path, ResourceChangeKind kind, string? before, string? after) =>
        sink.Add(new ResourceChange(path.Length == 0 ? "(whole object)" : path, kind, before, after));

    private static bool IsBookkeeping(string path) =>
        BookkeepingPaths.Any(p => path.Equals(p, StringComparison.Ordinal) || path.StartsWith(p + ".", StringComparison.Ordinal)
            || path.StartsWith(p + "[", StringComparison.Ordinal));

    /// <summary>
    /// Path segments read the way a manifest is written, except that a key containing a
    /// dot or a bracket — which every <c>app.kubernetes.io/*</c> label does — is quoted,
    /// or the path would be ambiguous about where the field name ends.
    /// </summary>
    private static string Join(string path, string name)
    {
        var segment = name.AsSpan().IndexOfAny(".[]'") >= 0 ? $"['{name}']" : name;
        return path.Length == 0
            ? segment
            : segment.StartsWith('[') ? path + segment : $"{path}.{segment}";
    }

    private static bool SameScalar(JsonElement a, JsonElement b) => a.ValueKind switch
    {
        JsonValueKind.String => string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal),
        _ => string.Equals(a.GetRawText(), b.GetRawText(), StringComparison.Ordinal),
    };

    private static bool SameJson(JsonElement a, JsonElement b) =>
        a.ValueKind == b.ValueKind && string.Equals(a.GetRawText(), b.GetRawText(), StringComparison.Ordinal);

    /// <summary>
    /// One value as the panel shows it: scalars as themselves, objects and arrays as
    /// compact JSON with their whitespace collapsed, everything capped — a diff row is a
    /// single line in a ~300px dock, and a whole container spec rendered in full would
    /// push every other row off it.
    /// </summary>
    private static string Render(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Null => "null",
            JsonValueKind.Object or JsonValueKind.Array => Collapse(value.GetRawText()),
            _ => value.GetRawText(),
        };

        return text.Length > MaxValueChars ? text[..MaxValueChars] + "…" : text;
    }

    private static string Collapse(string json)
    {
        var builder = new StringBuilder(Math.Min(json.Length, MaxValueChars + 1));
        var inString = false;
        var escaped = false;
        var pendingSpace = false;
        foreach (var c in json)
        {
            if (inString)
            {
                builder.Append(c);
                inString = escaped || c != '"';
                escaped = !escaped && c == '\\';
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
            if (c == '"')
            {
                inString = true;
                escaped = false;
            }

            if (builder.Length > MaxValueChars)
            {
                break;
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// The result of a server-side dry-run apply: what would change, and the object the
/// server says it would end up with. The object is kept because the diff is a
/// rendering of it — anything that later wants the whole previewed manifest (a
/// side-by-side view, a copy button) has it without a second round trip.
/// </summary>
public sealed record ApplyPreview(ResourceDiff Diff, DynamicResource Previewed);
