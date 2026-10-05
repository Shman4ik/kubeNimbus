namespace KubeNimbus.Core.Tests;

/// <summary>
/// <see cref="NamespaceWatch"/>'s tagging: every event carries the namespace whose watch
/// delivered it, and a namespace whose stream fails reports it and still answers with a
/// Synced, so a list waiting on every namespace is not left waiting on that one.
/// </summary>
public class NamespaceWatchTests
{
    private static async IAsyncEnumerable<ResourceEvent<DynamicResource>> Stream(bool fail)
    {
        yield return ResourceEvent<DynamicResource>.Reset;
        await Task.Yield();
        if (fail)
        {
            throw new HttpRequestException("forbidden", null, System.Net.HttpStatusCode.Forbidden);
        }

        yield return ResourceEvent<DynamicResource>.Synced;
    }

    private static async Task<List<NamespacedResourceEvent>> Collect(IAsyncEnumerable<NamespacedResourceEvent> source)
    {
        var events = new List<NamespacedResourceEvent>();
        await foreach (var evt in source)
        {
            events.Add(evt);
        }

        return events;
    }

    [Test]
    public async Task Events_carry_their_namespace()
    {
        var events = await Collect(NamespaceWatch.TagAsync(Stream(fail: false), "payments", null, CancellationToken.None));

        await Assert.That(events.Select(e => $"{e.Namespace}:{e.Event.Type}"))
            .IsEquivalentTo(["payments:Reset", "payments:Synced"]);
    }

    [Test]
    public async Task A_failed_namespace_is_reported_and_still_answers_with_synced()
    {
        string? failed = null;
        var events = await Collect(NamespaceWatch.TagAsync(Stream(fail: true), "secret-team", (ns, _) => failed = ns, CancellationToken.None));

        await Assert.That(failed).IsEqualTo("secret-team");
        await Assert.That(events.Select(e => e.Event.Type))
            .IsEquivalentTo([ResourceEventType.Reset, ResourceEventType.Synced]);
    }
}
