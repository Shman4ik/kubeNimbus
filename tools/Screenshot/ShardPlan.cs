namespace KubeNimbus.Screenshot;

/// <summary>
/// #347: splits the scenario list across CI runners. <c>--shard=i/n</c> (1-based: <c>1/5</c> to
/// <c>5/5</c>, the same number as the CI job's name and its artifact) renders the scenarios
/// whose 0-based position in the list is <c>i - 1</c> modulo <c>n</c>, so every scenario lands in exactly
/// one shard by construction and a scenario added anywhere in the list is picked up by some
/// shard without anyone assigning it. Round-robin rather than a hash of the name, because the
/// expensive scenarios sit next to each other in the list (the log panes, which wait for their
/// replayed streams to end) and round-robin deals them out evenly, where a hash can stack them.
///
/// <para>
/// The CI job derives the number from <c>strategy.job-index</c> and the count from
/// <c>strategy.job-total</c>, and refuses a matrix whose labels are not 1..n in order, so a
/// matrix that grows or shrinks can never leave a shard unrendered. <see cref="Verify"/>
/// checks the partition itself on every run.
/// </para>
/// </summary>
internal readonly record struct ShardPlan(int Number, int Count)
{
    internal static ShardPlan All => new(1, 1);

    /// <summary>The run's shard, set once from the command line before any scenario renders.</summary>
    internal static ShardPlan Current { get; set; } = All;

    internal bool IsSharded => Count > 1;

    /// <summary>Parses <c>--shard=i/n</c> out of <paramref name="args"/> and returns the rest.</summary>
    internal static (ShardPlan Plan, string[] Remaining) Parse(string[] args)
    {
        var plan = All;
        var rest = new List<string>();
        foreach (var arg in args)
        {
            if (!arg.StartsWith("--shard=", StringComparison.Ordinal))
            {
                rest.Add(arg);
                continue;
            }

            var parts = arg["--shard=".Length..].Split('/');
            if (parts.Length != 2
                || !int.TryParse(parts[0], out var number)
                || !int.TryParse(parts[1], out var count)
                || count < 1 || number < 1 || number > count)
            {
                throw new ArgumentException($"Expected --shard=<number>/<count> with 1 <= number <= count, got '{arg}'.");
            }

            plan = new ShardPlan(number, count);
        }

        return (plan, rest.ToArray());
    }

    internal bool Contains(int position) => position % Count == Number - 1;

    /// <summary>
    /// The share of a full run this shard renders, for the walks' "not vacuous" floors: a
    /// shard that renders a sixth of the scenarios is held to a sixth of each floor.
    /// </summary>
    internal int Floor(int fullRunFloor) => fullRunFloor / Count;

    /// <summary>
    /// Every position in exactly one shard, and no two scenarios with one name (the second
    /// would overwrite the first's PNG and its checks would report under the wrong name).
    /// </summary>
    internal void Verify(IReadOnlyList<string> names)
    {
        var duplicate = names.GroupBy(n => n, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Two scenarios are named '{duplicate.Key}'.");
        }

        for (var position = 0; position < names.Count; position++)
        {
            var owners = 0;
            for (var shard = 1; shard <= Count; shard++)
            {
                if (new ShardPlan(shard, Count).Contains(position))
                {
                    owners++;
                }
            }

            if (owners != 1)
            {
                throw new InvalidOperationException(
                    $"Scenario '{names[position]}' belongs to {owners} of {Count} shards; it must belong to exactly one.");
            }
        }
    }
}
