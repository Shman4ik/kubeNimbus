namespace KubeNimbus.Core;

/// <summary>What one line of a text diff is: present on both sides, only on the new
/// side, only on the old side, or a run of unchanged lines that was collapsed away.</summary>
public enum TextDiffKind
{
    Unchanged,
    Added,
    Removed,

    /// <summary>
    /// A placeholder standing in for a run of unchanged lines that was collapsed. It is
    /// produced only by <see cref="TextDiff.Collapse"/>, never by the diff itself, and it
    /// carries the number of lines it replaces so the panel can say how much is not shown.
    /// </summary>
    Skipped,
}

/// <summary>
/// One row of a rendered line diff: which side it belongs to, the line numbers it has on
/// each side (null where it has none), and the text itself.
/// </summary>
/// <param name="SkippedCount">Set only for <see cref="TextDiffKind.Skipped"/>.</param>
public sealed record TextDiffLine(
    TextDiffKind Kind,
    int? LeftNumber,
    int? RightNumber,
    string Text,
    int SkippedCount = 0);

/// <summary>
/// One row of a side-by-side diff. Either half may be null, which is the alignment filler
/// a two-column layout needs and the reason this is derived rather than laid out twice: a
/// deleted line on the left has to face a blank on the right, or the two columns stop
/// describing the same change.
/// </summary>
public sealed record TextDiffPair(TextDiffLine? Left, TextDiffLine? Right, int SkippedCount = 0)
{
    public bool IsSkipped => SkippedCount > 0;
}

/// <summary>
/// A line diff of two documents — the shape <c>git diff</c>, <c>kubectl diff</c> and VS
/// Code's diff editor all show, and the way a manifest change is actually read: the
/// document with its changed lines in place, rather than a list of field paths.
/// </summary>
/// <remarks>
/// <para>
/// The two documents are two serializations of nearly the same object, so almost every
/// line is shared. The common prefix and suffix are therefore trimmed first and the LCS
/// runs only over what is left, which is what keeps a 600-line CRD affordable when three
/// lines of it changed.
/// </para>
/// <para>
/// The LCS table is <c>(n+1) × (m+1)</c> ints, so it is bounded explicitly rather than
/// allocated on trust: past <see cref="DefaultCellBudget"/> cells the middle is reported
/// as one removal followed by one insertion and <see cref="IsApproximate"/> is set. That
/// is a state to render honestly — a diff that quietly stops aligning is worse than one
/// that says it stopped.
/// </para>
/// <para>
/// This lives in Core, with no UI type anywhere near it, for the same reason
/// <see cref="ResourceDiff"/> does: it is engine work, and a CLI would want the same
/// output.
/// </para>
/// </remarks>
public sealed class TextDiff
{
    /// <summary>How many unchanged lines are kept either side of a changed run.</summary>
    public const int DefaultContextLines = 3;

    /// <summary>
    /// The largest LCS table this will build, in cells. One million cells is a 4 MB int
    /// array and a middle section of ~1000 changed lines on each side, which is already
    /// far past a manifest anyone reads line by line.
    /// </summary>
    public const int DefaultCellBudget = 1_000_000;

    private TextDiff(IReadOnlyList<TextDiffLine> lines, int addedCount, int removedCount, bool isApproximate)
    {
        Lines = lines;
        AddedCount = addedCount;
        RemovedCount = removedCount;
        IsApproximate = isApproximate;
    }

    /// <summary>Every line of both documents, in reading order and never collapsed.</summary>
    public IReadOnlyList<TextDiffLine> Lines { get; }

    public int AddedCount { get; }

    public int RemovedCount { get; }

    /// <summary>
    /// True when the middle section was too large to align line by line and is reported as
    /// a wholesale replacement. Say so where it is shown.
    /// </summary>
    public bool IsApproximate { get; }

    /// <summary>True when the two documents are identical line for line.</summary>
    public bool IsEmpty => AddedCount == 0 && RemovedCount == 0;

    /// <summary>
    /// Diffs two documents by line. A null or empty <paramref name="before"/> is an empty
    /// document, which is what a create looks like: every line is an addition.
    /// </summary>
    public static TextDiff Between(string? before, string? after, int cellBudget = DefaultCellBudget)
    {
        var left = SplitLines(before);
        var right = SplitLines(after);

        var prefix = 0;
        while (prefix < left.Length && prefix < right.Length
            && string.Equals(left[prefix], right[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < left.Length - prefix && suffix < right.Length - prefix
            && string.Equals(left[^(suffix + 1)], right[^(suffix + 1)], StringComparison.Ordinal))
        {
            suffix++;
        }

        var lines = new List<TextDiffLine>(left.Length + right.Length);
        var leftNumber = 0;
        var rightNumber = 0;
        var added = 0;
        var removed = 0;

        for (var i = 0; i < prefix; i++)
        {
            lines.Add(new TextDiffLine(TextDiffKind.Unchanged, ++leftNumber, ++rightNumber, left[i]));
        }

        var leftMiddle = left[prefix..(left.Length - suffix)];
        var rightMiddle = right[prefix..(right.Length - suffix)];

        var cells = ((long)leftMiddle.Length + 1) * (rightMiddle.Length + 1);
        var approximate = cells > cellBudget;

        if (approximate)
        {
            foreach (var text in leftMiddle)
            {
                lines.Add(new TextDiffLine(TextDiffKind.Removed, ++leftNumber, null, text));
                removed++;
            }

            foreach (var text in rightMiddle)
            {
                lines.Add(new TextDiffLine(TextDiffKind.Added, null, ++rightNumber, text));
                added++;
            }
        }
        else
        {
            EmitLcs(leftMiddle, rightMiddle, lines, ref leftNumber, ref rightNumber, ref added, ref removed);
        }

        for (var i = 0; i < suffix; i++)
        {
            var text = left[left.Length - suffix + i];
            lines.Add(new TextDiffLine(TextDiffKind.Unchanged, ++leftNumber, ++rightNumber, text));
        }

        return new TextDiff(lines, added, removed, approximate);
    }

    /// <summary>
    /// The diff with long unchanged runs replaced by one <see cref="TextDiffKind.Skipped"/>
    /// row each, keeping <paramref name="contextLines"/> lines either side of every change.
    /// Collapsing is not a nicety here: a Deployment serializes to ~60 lines and a CRD to
    /// several hundred, and the panel it renders into is a ~300px dock.
    /// </summary>
    /// <remarks>
    /// A run of one line is kept rather than collapsed — replacing a single line with a
    /// "1 unchanged line" separator saves no height and loses the line.
    /// </remarks>
    public IReadOnlyList<TextDiffLine> Collapse(int contextLines = DefaultContextLines)
    {
        if (contextLines < 0)
        {
            contextLines = 0;
        }

        var keep = new bool[Lines.Count];
        for (var i = 0; i < Lines.Count; i++)
        {
            if (Lines[i].Kind == TextDiffKind.Unchanged)
            {
                continue;
            }

            var from = Math.Max(0, i - contextLines);
            var to = Math.Min(Lines.Count - 1, i + contextLines);
            for (var j = from; j <= to; j++)
            {
                keep[j] = true;
            }
        }

        var result = new List<TextDiffLine>(Lines.Count);
        var index = 0;
        while (index < Lines.Count)
        {
            if (keep[index])
            {
                result.Add(Lines[index++]);
                continue;
            }

            var start = index;
            while (index < Lines.Count && !keep[index])
            {
                index++;
            }

            var length = index - start;
            if (length <= 1)
            {
                for (var j = start; j < index; j++)
                {
                    result.Add(Lines[j]);
                }
            }
            else
            {
                result.Add(new TextDiffLine(TextDiffKind.Skipped, null, null, "", length));
            }
        }

        return result;
    }

    /// <summary>
    /// The same rows as two aligned columns. Derived from one row list on purpose: two
    /// independently built layouts can disagree about what changed, and the one thing a
    /// diff may not do is describe the change differently depending on how it is shown.
    /// </summary>
    public static IReadOnlyList<TextDiffPair> SideBySide(IReadOnlyList<TextDiffLine> rows)
    {
        var pairs = new List<TextDiffPair>(rows.Count);
        var index = 0;
        while (index < rows.Count)
        {
            var row = rows[index];
            switch (row.Kind)
            {
                case TextDiffKind.Unchanged:
                    pairs.Add(new TextDiffPair(row, row));
                    index++;
                    continue;

                case TextDiffKind.Skipped:
                    pairs.Add(new TextDiffPair(null, null, row.SkippedCount));
                    index++;
                    continue;
            }

            // A removed run and the added run that follows it are one change seen from two
            // sides, so they are zipped; whichever side runs out first gets fillers.
            var removed = new List<TextDiffLine>();
            while (index < rows.Count && rows[index].Kind == TextDiffKind.Removed)
            {
                removed.Add(rows[index++]);
            }

            var added = new List<TextDiffLine>();
            while (index < rows.Count && rows[index].Kind == TextDiffKind.Added)
            {
                added.Add(rows[index++]);
            }

            for (var i = 0; i < Math.Max(removed.Count, added.Count); i++)
            {
                pairs.Add(new TextDiffPair(
                    i < removed.Count ? removed[i] : null,
                    i < added.Count ? added[i] : null));
            }
        }

        return pairs;
    }

    private static void EmitLcs(
        string[] left,
        string[] right,
        List<TextDiffLine> sink,
        ref int leftNumber,
        ref int rightNumber,
        ref int added,
        ref int removed)
    {
        var n = left.Length;
        var m = right.Length;

        // Length of the longest common subsequence of the two suffixes starting at (i, j).
        // Filled from the end so the emit walk below can run forwards, which is what keeps
        // the output in document order.
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(left[i], right[j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var x = 0;
        var y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(left[x], right[y], StringComparison.Ordinal))
            {
                sink.Add(new TextDiffLine(TextDiffKind.Unchanged, ++leftNumber, ++rightNumber, left[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                // Removals lead when the two are equally good, so a replaced line reads as
                // the old line above the new one — which is the order every diff is read in.
                sink.Add(new TextDiffLine(TextDiffKind.Removed, ++leftNumber, null, left[x]));
                removed++;
                x++;
            }
            else
            {
                sink.Add(new TextDiffLine(TextDiffKind.Added, null, ++rightNumber, right[y]));
                added++;
                y++;
            }
        }

        while (x < n)
        {
            sink.Add(new TextDiffLine(TextDiffKind.Removed, ++leftNumber, null, left[x++]));
            removed++;
        }

        while (y < m)
        {
            sink.Add(new TextDiffLine(TextDiffKind.Added, null, ++rightNumber, right[y++]));
            added++;
        }
    }

    /// <summary>
    /// Splits a document into lines, tolerating CRLF and ignoring the trailing newline a
    /// serializer leaves behind — otherwise every document would end with a phantom empty
    /// line that shows up in the diff whenever only one side has it.
    /// </summary>
    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.EndsWith('\n'))
        {
            normalized = normalized[..^1];
        }

        return normalized.Split('\n');
    }
}
