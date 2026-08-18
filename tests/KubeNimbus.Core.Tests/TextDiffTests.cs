using System.Text.Json;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// The line diff behind the apply preview's Diff and Split views. Pure by construction —
/// two strings in, one row list out — so all of it is testable without a cluster, which
/// is the reason it lives in Core at all.
///
/// <para>
/// Every failure here is quiet rather than loud. A diff that pairs lines by index reports
/// one inserted line as a rewrite of everything below it; a collapse that miscounts hides
/// part of the document while claiming to be complete; an unbounded LCS does not fail, it
/// allocates until the app stops. All three look like a working panel until the output is
/// read.
/// </para>
/// </summary>
public class TextDiffTests
{
    /// <summary>
    /// One row per line, rendered the way a unified diff reads: sign, the two line
    /// numbers, then the text. Asserting on this rather than on counts is deliberate — a
    /// count cannot tell an insert from a replace, which is the distinction most of these
    /// tests are about.
    /// </summary>
    private static string Describe(IEnumerable<TextDiffLine> lines) =>
        string.Join(" | ", lines.Select(l => l.Kind switch
        {
            TextDiffKind.Added => $"+{l.RightNumber}:{l.Text}",
            TextDiffKind.Removed => $"-{l.LeftNumber}:{l.Text}",
            TextDiffKind.Skipped => $"…{l.SkippedCount}",
            _ => $" {l.LeftNumber}/{l.RightNumber}:{l.Text}",
        }));

    [Test]
    public async Task Identical_documents_produce_no_change()
    {
        var diff = TextDiff.Between("a\nb\nc", "a\nb\nc");

        await Assert.That(diff.IsEmpty).IsTrue();
        await Assert.That(diff.AddedCount).IsEqualTo(0);
        await Assert.That(diff.RemovedCount).IsEqualTo(0);
        await Assert.That(diff.Lines.Count).IsEqualTo(3);
        await Assert.That(diff.Lines.All(l => l.Kind == TextDiffKind.Unchanged)).IsTrue();
    }

    /// <summary>
    /// The distinction the whole engine exists for. Pairing by index would report the
    /// inserted line and every line after it as rewritten — which is exactly what a diff
    /// of a manifest must not do, since a manifest is mostly unchanged by definition.
    /// </summary>
    [Test]
    public async Task An_inserted_line_is_an_insert_and_not_a_rewrite()
    {
        var diff = TextDiff.Between("a\nb\nc", "a\nNEW\nb\nc");

        await Assert.That(Describe(diff.Lines))
            .IsEqualTo(" 1/1:a | +2:NEW |  2/3:b |  3/4:c");
        await Assert.That(diff.AddedCount).IsEqualTo(1);
        await Assert.That(diff.RemovedCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_deleted_line_is_a_delete_and_not_a_rewrite()
    {
        var diff = TextDiff.Between("a\nb\nc", "a\nc");

        await Assert.That(Describe(diff.Lines)).IsEqualTo(" 1/1:a | -2:b |  3/2:c");
        await Assert.That(diff.RemovedCount).IsEqualTo(1);
        await Assert.That(diff.AddedCount).IsEqualTo(0);
    }

    /// <summary>A replaced line reads old-above-new, the order every diff is read in.</summary>
    [Test]
    public async Task A_replaced_line_shows_the_old_line_above_the_new_one()
    {
        var diff = TextDiff.Between("a\nb\nc", "a\nB\nc");

        await Assert.That(Describe(diff.Lines)).IsEqualTo(" 1/1:a | -2:b | +2:B |  3/3:c");
    }

    /// <summary>
    /// The case trimming cannot rescue, and therefore the one that pins the alignment
    /// itself: a change at the top, a change at the bottom, and an insert in between — a
    /// manifest with an edited label and an edited replica count is exactly this shape.
    /// Pairing the middle by index reports every line after the insert as rewritten.
    /// </summary>
    [Test]
    public async Task An_insert_between_two_changed_ends_leaves_the_middle_untouched()
    {
        var diff = TextDiff.Between(
            "first\nkeep-1\nkeep-2\nkeep-3\nlast",
            "first-changed\nkeep-1\nNEW\nkeep-2\nkeep-3\nlast-changed");

        await Assert.That(Describe(diff.Lines)).IsEqualTo(
            "-1:first | +1:first-changed"
            + " |  2/2:keep-1 | +3:NEW |  3/4:keep-2 |  4/5:keep-3"
            + " | -5:last | +6:last-changed");
        await Assert.That(diff.AddedCount).IsEqualTo(3);
        await Assert.That(diff.RemovedCount).IsEqualTo(2);
    }

    [Test]
    public async Task A_change_on_the_very_first_line_is_found()
    {
        var diff = TextDiff.Between("a\nb\nc", "A\nb\nc");

        await Assert.That(Describe(diff.Lines)).IsEqualTo("-1:a | +1:A |  2/2:b |  3/3:c");
    }

    [Test]
    public async Task A_change_on_the_very_last_line_is_found()
    {
        var diff = TextDiff.Between("a\nb\nc", "a\nb\nC");

        await Assert.That(Describe(diff.Lines)).IsEqualTo(" 1/1:a |  2/2:b | -3:c | +3:C");
    }

    /// <summary>
    /// A create: there is no live object, so every line is an addition. The null side is
    /// what <c>ResourceDiff.ToDiffableYaml</c> returns for an object that is not there yet.
    /// </summary>
    [Test]
    public async Task An_empty_left_side_is_all_additions()
    {
        var diff = TextDiff.Between(null, "a\nb");

        await Assert.That(Describe(diff.Lines)).IsEqualTo("+1:a | +2:b");
        await Assert.That(diff.AddedCount).IsEqualTo(2);
    }

    /// <summary>
    /// A serializer's trailing newline is not a line. Without this, a document that ends
    /// with one and a document that does not would differ on a phantom empty line that
    /// nobody wrote.
    /// </summary>
    [Test]
    public async Task A_trailing_newline_is_not_a_line_and_CRLF_is_not_a_change()
    {
        await Assert.That(TextDiff.Between("a\nb\n", "a\nb").IsEmpty).IsTrue();
        await Assert.That(TextDiff.Between("a\r\nb\r\n", "a\nb").IsEmpty).IsTrue();
        await Assert.That(TextDiff.Between("", "").Lines.Count).IsEqualTo(0);
    }

    /// <summary>
    /// The budget. Past it the middle is reported as a wholesale replacement and
    /// <see cref="TextDiff.IsApproximate"/> says so — a diff that silently stops aligning
    /// is worse than one that admits it, and an unbounded LCS table is how a 5000-line
    /// CRD takes the app down instead.
    /// </summary>
    [Test]
    public async Task Past_the_cell_budget_the_middle_is_reported_as_a_replacement()
    {
        var before = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"old-{i}"));
        var after = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"new-{i}"));

        var bounded = TextDiff.Between(before, after, cellBudget: 16);

        await Assert.That(bounded.IsApproximate).IsTrue();
        await Assert.That(bounded.RemovedCount).IsEqualTo(40);
        await Assert.That(bounded.AddedCount).IsEqualTo(40);
        await Assert.That(bounded.Lines.Take(40).All(l => l.Kind == TextDiffKind.Removed)).IsTrue();
        await Assert.That(bounded.Lines.Skip(40).All(l => l.Kind == TextDiffKind.Added)).IsTrue();

        // The same pair inside the budget aligns properly and says it did.
        await Assert.That(TextDiff.Between(before, after).IsApproximate).IsFalse();
    }

    /// <summary>
    /// The budget is spent on the middle, not on the document: two 5000-line documents
    /// that differ in one line are trimmed to a middle of one line each and align exactly,
    /// which is the case this feature actually meets.
    /// </summary>
    [Test]
    public async Task A_long_document_with_one_change_still_aligns()
    {
        var lines = Enumerable.Range(0, 5_000).Select(i => $"line-{i}").ToArray();
        var before = string.Join("\n", lines);
        lines[2_500] = "line-2500-changed";
        var after = string.Join("\n", lines);

        var diff = TextDiff.Between(before, after);

        await Assert.That(diff.IsApproximate).IsFalse();
        await Assert.That(diff.AddedCount).IsEqualTo(1);
        await Assert.That(diff.RemovedCount).IsEqualTo(1);
    }

    /// <summary>
    /// Collapsing keeps the context lines either side of a change and states the size of
    /// what it took away. A gap that does not say how big it is turns the panel into a
    /// document with silent holes in it.
    /// </summary>
    [Test]
    public async Task Unchanged_runs_collapse_to_a_counted_gap_with_context_either_side()
    {
        var before = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line-{i}"));
        var after = before.Replace("line-10", "line-10-changed", StringComparison.Ordinal);

        var rows = TextDiff.Between(before, after).Collapse();

        await Assert.That(Describe(rows)).IsEqualTo(
            "…6"
            + " |  7/7:line-7 |  8/8:line-8 |  9/9:line-9"
            + " | -10:line-10 | +10:line-10-changed"
            + " |  11/11:line-11 |  12/12:line-12 |  13/13:line-13"
            + " | …7");
    }

    /// <summary>
    /// A run of one line is kept rather than collapsed: a "1 unchanged line" separator is
    /// the same height as the line it replaces and tells the reader less.
    /// </summary>
    [Test]
    public async Task A_gap_of_one_line_is_shown_rather_than_collapsed()
    {
        var before = "a\nb\nc\nd\ne\nf\ng\nh\ni";
        var after = "A\nb\nc\nd\ne\nf\ng\nh\nI";

        var rows = TextDiff.Between(before, after).Collapse(contextLines: 4);

        await Assert.That(rows.Any(r => r.Kind == TextDiffKind.Skipped)).IsFalse();
        await Assert.That(rows.Count).IsEqualTo(11);
    }

    /// <summary>A diff with nothing in it collapses to one gap covering the whole document.</summary>
    [Test]
    public async Task An_unchanged_document_collapses_to_a_single_gap()
    {
        var text = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"line-{i}"));

        var rows = TextDiff.Between(text, text).Collapse();

        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Kind).IsEqualTo(TextDiffKind.Skipped);
        await Assert.That(rows[0].SkippedCount).IsEqualTo(12);
    }

    /// <summary>
    /// Side by side is the same rows in two columns, and the fillers are the point: a
    /// deleted line has to face a blank, or the two columns stop describing one change.
    /// </summary>
    [Test]
    public async Task Side_by_side_pairs_a_replacement_and_fills_the_uneven_half()
    {
        var rows = TextDiff.Between("a\nb\nc\nd", "a\nB\nB2\nd").Collapse(contextLines: 3);

        var pairs = TextDiff.SideBySide(rows);

        await Assert.That(string.Join(" | ", pairs.Select(p =>
            $"{p.Left?.Text ?? "·"}/{p.Right?.Text ?? "·"}")))
            .IsEqualTo("a/a | b/B | c/B2 | d/d");

        // One removal against two additions: the second addition faces a filler.
        var uneven = TextDiff.SideBySide(TextDiff.Between("a\nb\nz", "a\nB\nB2\nz").Collapse());
        await Assert.That(string.Join(" | ", uneven.Select(p =>
            $"{p.Left?.Text ?? "·"}/{p.Right?.Text ?? "·"}")))
            .IsEqualTo("a/a | b/B | ·/B2 | z/z");
    }

    /// <summary>A collapsed run stays one row in the side-by-side view, spanning both columns.</summary>
    [Test]
    public async Task Side_by_side_keeps_a_gap_as_one_row()
    {
        var before = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line-{i}"));
        var after = before.Replace("line-10", "line-10-changed", StringComparison.Ordinal);

        var pairs = TextDiff.SideBySide(TextDiff.Between(before, after).Collapse());

        var gaps = pairs.Where(p => p.IsSkipped).ToArray();
        await Assert.That(gaps.Length).IsEqualTo(2);
        await Assert.That(gaps.All(g => g.Left is null && g.Right is null)).IsTrue();
        await Assert.That(gaps[0].SkippedCount).IsEqualTo(6);
    }

    /// <summary>
    /// The bookkeeping fields are stripped from both documents before they are diffed.
    /// <c>managedFields</c> alone is routinely a third of a real object and changes on
    /// every apply, so a text diff over the raw documents would open on the one section
    /// nobody wants to read.
    /// </summary>
    [Test]
    public async Task Bookkeeping_fields_never_reach_the_text_diff()
    {
        var live = JsonDocument.Parse("""
            {"apiVersion":"v1","kind":"ConfigMap",
             "metadata":{"name":"web","resourceVersion":"1","generation":4,
                         "managedFields":[{"manager":"kubectl"}]},
             "data":{"level":"info"}}
            """).RootElement.Clone();
        var previewed = JsonDocument.Parse("""
            {"apiVersion":"v1","kind":"ConfigMap",
             "metadata":{"name":"web","resourceVersion":"2","generation":5,
                         "managedFields":[{"manager":"kubenimbus"}]},
             "data":{"level":"debug"}}
            """).RootElement.Clone();

        var left = ResourceDiff.ToDiffableYaml(live);
        var right = ResourceDiff.ToDiffableYaml(previewed);

        await Assert.That(left).DoesNotContain("managedFields");
        await Assert.That(left).DoesNotContain("resourceVersion");
        await Assert.That(left).DoesNotContain("generation");
        await Assert.That(left).Contains("name: web");

        var diff = TextDiff.Between(left, right);

        await Assert.That(diff.AddedCount).IsEqualTo(1);
        await Assert.That(diff.RemovedCount).IsEqualTo(1);
        await Assert.That(diff.Lines.Single(l => l.Kind == TextDiffKind.Added).Text).Contains("debug");
    }

    /// <summary>A missing object is an empty document, which is what makes a create read as all-added.</summary>
    [Test]
    public async Task A_missing_object_renders_as_an_empty_document()
    {
        await Assert.That(ResourceDiff.ToDiffableYaml(null)).IsEqualTo("");
    }
}
