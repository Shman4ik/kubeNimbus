using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KubeNimbus.Core.Tests.Live;

/// <summary>
/// VER-23: FEAT-2's printer columns against a real API server, column for column with
/// <c>kubectl get</c>. The reference is the API server's own <c>Table</c> rendering, which
/// is exactly what kubectl asks for and prints — so "matches kubectl" is checked without a
/// kubectl binary, and against every CRD on the cluster rather than only the ones this repo
/// wrote. Read-only: nothing here changes an object.
/// </summary>
public partial class PrinterColumnsLiveTests
{
    /// <summary>The sandbox's own Widget CRD: string, integer, boolean, an unresolvable path and a priority-1 column.</summary>
    [Test]
    [Timeout(60_000)]
    public async Task Shop_widgets_match_kubectl_get_column_for_column(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var mismatches = await CompareAsync(client, LiveCluster.Widgets, "demo-shop", ct);
        await Assert.That(mismatches.Rows).IsGreaterThanOrEqualTo(3);
        await Assert.That(string.Join('\n', mismatches.Problems)).IsEmpty();

        // `-o wide`: the priority-1 column is declared, carried, and evaluated like the rest.
        var columns = await client.GetPrinterColumnsAsync(LiveCluster.Widgets, ct);
        await Assert.That(columns.Single(c => c.Name == "Colour").Priority).IsEqualTo(1);
    }

    /// <summary>
    /// The sandbox's Backup CRD, cluster-scoped: a condition filter, a <c>type: date</c>
    /// column that is not the creation timestamp, and a declared Age — which the list folds
    /// into its own Age column rather than drawing twice.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task Backups_match_kubectl_get_and_the_declared_age_is_the_one_folded_away(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        var mismatches = await CompareAsync(client, LiveCluster.Backups, null, ct);
        await Assert.That(mismatches.Rows).IsGreaterThanOrEqualTo(3);
        await Assert.That(string.Join('\n', mismatches.Problems)).IsEmpty();

        var all = await client.GetPrinterColumnsAsync(LiveCluster.Backups, ct);
        var visible = PrinterColumns.Visible(all, includeLowPriority: true, max: 10);
        await Assert.That(all.Select(c => c.Name).Except(visible.Select(c => c.Name)).ToArray())
            .IsEquivalentTo(new[] { "Age" });

        // The non-creationTimestamp date is one the shared age timer can re-render.
        var nightly = await client.ReadResourceAsync(LiveCluster.Backups, null, "nightly", ct);
        await Assert.That(PrinterColumns.DateValue(all.Single(c => c.Name == "Last run"), nightly!.Raw)).IsNotNull();
    }

    /// <summary>
    /// A CRD that declares no columns degrades to exactly the list the app already draws:
    /// the server answers with Name and Age (its <c>serveDefaultColumnsIfEmpty</c>), and the
    /// app reads no printer columns at all, keeping its own Name and Age.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task A_crd_declaring_no_columns_lists_as_name_and_age(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        await Assert.That(await client.GetPrinterColumnsAsync(LiveCluster.FactoryWidgets, ct)).IsEmpty();

        using var table = await LiveCluster.GetTableAsync(
            client, LiveCluster.FactoryWidgets.CollectionPath("demo-data"), ct);
        await Assert.That(table.Columns.Select(c => c.Name).ToArray()).IsEquivalentTo(new[] { "Name", "Age" });
    }

    /// <summary>
    /// Every CRD on the cluster that has objects and declares columns — k3s's own
    /// (<c>helm.cattle.io</c>, <c>k3s.cattle.io</c>), Argo CD's, Gateway API's and whatever else is
    /// installed — compared column for column with the server's own rendering. This is the
    /// positive case the backlog row names: CRDs this repo did not author, whose condition
    /// filters and paths nobody here chose.
    /// </summary>
    [Test]
    [Timeout(180_000)]
    public async Task Every_installed_crd_with_objects_matches_kubectl_get(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        using var crds = await client.GetJsonDocumentAsync("apis/apiextensions.k8s.io/v1/customresourcedefinitions", ct);

        var problems = new List<string>();
        var compared = new List<string>();
        foreach (var crd in crds.RootElement.GetProperty("items").EnumerateArray())
        {
            var spec = crd.GetProperty("spec");
            var names = spec.GetProperty("names");
            foreach (var version in spec.GetProperty("versions").EnumerateArray())
            {
                if (!version.GetProperty("served").GetBoolean()
                    || !version.TryGetProperty("additionalPrinterColumns", out var declared)
                    || declared.GetArrayLength() == 0)
                {
                    continue;
                }

                var descriptor = new ResourceDescriptor(
                    spec.GetProperty("group").GetString()!,
                    version.GetProperty("name").GetString()!,
                    names.GetProperty("kind").GetString()!,
                    names.GetProperty("plural").GetString()!,
                    names.TryGetProperty("singular", out var s) ? s.GetString() ?? "" : "",
                    spec.GetProperty("scope").GetString() == "Namespaced",
                    [], []);

                var result = await CompareAsync(client, descriptor, null, ct);
                if (result.Rows > 0)
                {
                    compared.Add($"{descriptor.Plural}.{descriptor.Group}/{descriptor.Version} ({result.Rows})");
                }

                problems.AddRange(result.Problems);
            }
        }

        Console.WriteLine($"compared: {string.Join(", ", compared)}");

        // The sandbox always carries at least its own two plus k3s's HelmCharts and Addons.
        await Assert.That(compared.Count).IsGreaterThanOrEqualTo(4);
        await Assert.That(string.Join('\n', problems)).IsEmpty();
    }

    /// <summary>
    /// Where the two disagree, and why the app keeps its answer. The sandbox's Widget CRD
    /// declares its Colour column as <c>.metadata.labels['shop.kubenimbus.io/colour']</c>;
    /// a real API server leaves that cell empty for every row (<c>kubectl get -o wide</c>
    /// shows a blank COLOUR column), because kubectl's JSONPath does not treat a quoted,
    /// dotted key as one key. The form it does resolve is the escaped one, and the app now
    /// resolves that too. The app goes on resolving the bracketed form: it can only ever
    /// show a value the object really has.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task A_bracketed_dotted_key_is_blank_in_kubectl_and_the_escaped_form_is_not(CancellationToken ct)
    {
        using var client = await LiveCluster.ConnectAsync(ct);
        using var table = await LiveCluster.GetTableAsync(client, LiveCluster.Widgets.CollectionPath("demo-shop"), ct);
        var blue = await client.ReadResourceAsync(LiveCluster.Widgets, "demo-shop", "blue-widget", ct);

        await Assert.That(ServerTable.Text(table.Row("demo-shop/blue-widget")[table.IndexOf("Colour")])).IsEqualTo("");

        var bracketed = new PrinterColumn("Colour", "string", ".metadata.labels['shop.kubenimbus.io/colour']");
        var escaped = new PrinterColumn("Colour", "string", @".metadata.labels.shop\.kubenimbus\.io/colour");
        await Assert.That(PrinterColumns.Evaluate(bracketed, blue!.Raw)).IsEqualTo("blue");
        await Assert.That(PrinterColumns.Evaluate(escaped, blue.Raw)).IsEqualTo("blue");
    }

    private sealed record Comparison(int Rows, IReadOnlyList<string> Problems);

    /// <summary>
    /// One kind's list, both ways: the columns the app reads off the CRD against the
    /// Table's column definitions, then every cell of every row.
    /// </summary>
    private static async Task<Comparison> CompareAsync(
        ClusterClient client, ResourceDescriptor descriptor, string? @namespace, CancellationToken ct)
    {
        var kind = $"{descriptor.Plural}.{descriptor.Group}/{descriptor.Version}";
        var problems = new List<string>();

        var columns = await client.GetPrinterColumnsAsync(descriptor, ct);
        using var table = await LiveCluster.GetTableAsync(client, descriptor.CollectionPath(@namespace), ct);
        var objects = await client.ListResourceOnceAsync(descriptor, @namespace, cancellationToken: ct);
        var now = DateTimeOffset.UtcNow;

        // Name first, then the declared columns in declaration order, priorities and all.
        var serverColumns = table.Columns.Skip(1).Select(c => (c.Name, c.Priority)).ToArray();
        var ourColumns = columns.Select(c => (c.Name, c.Priority)).ToArray();
        if (table.Columns.Count == 0 || table.Columns[0].Name != "Name" || !serverColumns.SequenceEqual(ourColumns))
        {
            problems.Add($"{kind}: columns differ — server [{string.Join(", ", table.Columns.Select(c => $"{c.Name}:{c.Priority}"))}], "
                + $"app [Name, {string.Join(", ", ourColumns.Select(c => $"{c.Name}:{c.Priority}"))}]");
            return new Comparison(objects.Count, problems);
        }

        foreach (var obj in objects)
        {
            var key = $"{(descriptor.Namespaced ? obj.Namespace : "")}/{obj.Name}";
            var row = table.Rows.FirstOrDefault(r => r.Key == key || r.Key == $"?/{obj.Name}").Cells;
            if (row is null)
            {
                // Created or deleted between the two reads.
                continue;
            }

            for (var i = 0; i < columns.Count; i++)
            {
                if (CellProblem(columns[i], row[i + 1], obj.Raw, now) is { } problem)
                {
                    problems.Add($"{kind} {key} column \"{columns[i].Name}\" ({columns[i].JsonPath}): {problem}");
                }
            }
        }

        return new Comparison(objects.Count, problems);
    }

    /// <summary>
    /// Null when the app's cell says what kubectl's does. Exact for every type but
    /// <c>date</c>, where the app deliberately prints one unit (<see cref="RelativeTime"/>)
    /// and kubectl prints its <c>HumanDuration</c> — so there the check is that both read
    /// the <em>same timestamp</em>: kubectl's text, parsed back, agrees with the elapsed
    /// time the app computed, to within the precision kubectl printed.
    /// </summary>
    private static string? CellProblem(PrinterColumn column, JsonElement serverCell, JsonElement obj, DateTimeOffset now)
    {
        var ours = PrinterColumns.Evaluate(column, obj, now);
        var theirs = ServerTable.Text(serverCell);

        // The one known divergence, pinned by its own test below: kubectl's JSONPath does
        // not resolve a bracketed key containing a dot, the app does.
        if (theirs.Length == 0 && BracketedDottedKey().IsMatch(column.JsonPath))
        {
            return null;
        }

        if (column.Type != "date" || theirs is "" or "<unknown>" or "<invalid>")
        {
            return ours == theirs ? null : $"kubectl \"{theirs}\", app \"{ours}\"";
        }

        if (PrinterColumns.DateValue(column, obj) is not { } at)
        {
            return $"kubectl \"{theirs}\", app has no date";
        }

        var elapsed = now - at;
        if (ours != RelativeTime.Compact(elapsed))
        {
            return $"app \"{ours}\" is not the compact form of {elapsed}";
        }

        if (ParseHumanDuration(theirs) is not { } parsed)
        {
            return $"kubectl \"{theirs}\" is not a duration";
        }

        var precision = SmallestUnit(theirs) + TimeSpan.FromSeconds(5);
        return (elapsed - parsed).Duration() <= precision
            ? null
            : $"kubectl \"{theirs}\" ({parsed}) and app \"{ours}\" ({elapsed}) read different times";
    }

    [GeneratedRegex(@"\[\s*['""][^'""]*\.[^'""]*['""]\s*\]")]
    private static partial Regex BracketedDottedKey();

    [GeneratedRegex(@"(\d+)([ydhms])")]
    private static partial Regex DurationPart();

    private static TimeSpan UnitOf(string unit) => unit switch
    {
        "y" => TimeSpan.FromDays(365),
        "d" => TimeSpan.FromDays(1),
        "h" => TimeSpan.FromHours(1),
        "m" => TimeSpan.FromMinutes(1),
        _ => TimeSpan.FromSeconds(1),
    };

    private static TimeSpan? ParseHumanDuration(string text)
    {
        var parts = DurationPart().Matches(text);
        if (parts.Count == 0 || string.Concat(parts.Select(p => p.Value)) != text)
        {
            return null;
        }

        return parts.Aggregate(TimeSpan.Zero, (sum, p) =>
            sum + UnitOf(p.Groups[2].Value) * int.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    private static TimeSpan SmallestUnit(string text) =>
        DurationPart().Matches(text).Select(p => UnitOf(p.Groups[2].Value)).Min();
}
