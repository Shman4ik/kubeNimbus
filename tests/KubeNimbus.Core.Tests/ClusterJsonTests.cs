using System.Text;
using System.Text.Json;
using static KubeNimbus.Core.Tests.NodeWatchHttpTests;

namespace KubeNimbus.Core.Tests;

/// <summary>
/// One object nested deeper than <see cref="JsonDocument"/>'s default 64 levels — an Argo
/// Application's <c>valuesObject</c> is free-form — used to make every list and watch of its
/// kind throw, reported as a lost connection and retried for ever. Now the depth limit is
/// <see cref="ClusterJson.MaxDepth"/>, and an object past even that costs that object.
/// </summary>
[NotInParallel(nameof(ClusterJsonTests))]
public class ClusterJsonTests
{
    private static readonly ResourceDescriptor Applications = new(
        "argoproj.io", "v1alpha1", "Application", "applications", "application", Namespaced: true, ShortNames: [], Categories: []);

    /// <summary>An Application whose <c>spec.source.helm.valuesObject</c> is <paramref name="depth"/> objects deep.</summary>
    internal static string App(string name, int depth, string resourceVersion = "5")
    {
        var values = new StringBuilder();
        for (var i = 0; i < depth; i++)
        {
            values.Append("{\"a\":");
        }

        values.Append('1');
        values.Append('}', depth);
        return $$"""{"apiVersion":"argoproj.io/v1alpha1","kind":"Application","metadata":{"name":"{{name}}","namespace":"argocd","resourceVersion":"{{resourceVersion}}"},"spec":{"source":{"helm":{"valuesObject":"""
            + values + "}}}}";
    }

    private static string List(params string[] items) =>
        $$"""{"kind":"ApplicationList","apiVersion":"argoproj.io/v1alpha1","metadata":{"resourceVersion":"9"},"items":[{{string.Join(",", items)}}]}""";

    private static string Frame(string type, string obj) => $$"""{"type":"{{type}}","object":{{obj}}}""" + "\n";

    [Test]
    public async Task An_object_a_hundred_levels_deep_parses_and_renders_as_yaml_and_a_diff()
    {
        using var document = ClusterJson.Parse(App("deep", 100));
        var resource = DynamicResource.FromListItem(document.RootElement, Applications);

        await Assert.That(resource.Name).IsEqualTo("deep");
        await Assert.That(resource.ToYaml()).Contains("valuesObject");

        using var other = ClusterJson.Parse(App("deep", 100).Replace("\"a\":1", "\"a\":2", StringComparison.Ordinal));
        var diff = ResourceDiff.Between(document.RootElement, other.RootElement);
        await Assert.That(diff.Changes.Count).IsEqualTo(1);
    }

    [Test]
    public async Task The_recursive_readers_survive_the_whole_depth_limit()
    {
        // ClusterJson.MaxDepth less the four levels the Application itself takes.
        using var document = ClusterJson.Parse(App("limit", ClusterJson.MaxDepth - 5));
        var yaml = YamlJson.ToYamlString(document.RootElement);
        var back = YamlJson.ParseYamlToJson(yaml);

        await Assert.That(back!.ToJsonString()).IsEqualTo(document.RootElement.GetRawText());
        await Assert.That(() => ClusterJson.Parse(App("past", ClusterJson.MaxDepth + 1))).Throws<JsonException>();
    }

    [Test]
    public async Task A_list_and_watch_of_deep_objects_deliver_them_and_skip_only_what_cannot_be_read()
    {
        var skipped = new List<string>();
        await using var server = new ConcurrentStubServer(async (request, response) =>
        {
            if (request.Path == "/apis/argoproj.io/v1alpha1/applications" && !request.IsWatch)
            {
                await response.WriteAsync(List(App("deep", 100), App("too-deep", 400), App("plain", 1)));
                return;
            }

            if (request.Path == "/apis/argoproj.io/v1alpha1/applications" && request.IsWatch)
            {
                await response.StreamAsync(
                    Frame("ADDED", App("watched-too-deep", 400, "10"))
                    + "not json at all\n"
                    + Frame("ADDED", App("after", 3, "11")));
                return;
            }

            await response.NotFoundAsync();
        });

        using var client = server.Connect();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var names = new List<string>();
        await foreach (var evt in client.WatchResourceAsync(
                           Applications, connectionLost: ex =>
                           {
                               lock (skipped)
                               {
                                   skipped.Add($"{ex.GetType().Name}: {ex.Message}");
                               }
                           },
                           cancellationToken: cts.Token))
        {
            if (evt.Resource is { } app)
            {
                names.Add(app.Name);
            }

            if (names.Contains("after"))
            {
                break;
            }
        }

        await Assert.That(string.Join(",", names)).IsEqualTo("deep,plain,after");
        await Assert.That(skipped.Count).IsEqualTo(3);
        await Assert.That(skipped.All(s => s.StartsWith(nameof(UnreadableObjectException), StringComparison.Ordinal))).IsTrue();
        await Assert.That(skipped[0]).Contains("Application argocd/too-deep (nested deeper than 256 levels)");
        await Assert.That(skipped[1]).Contains("Application argocd/watched-too-deep");
        await Assert.That(skipped[2]).Contains("not valid JSON");

        // One list and one watch: nothing was retried, because nothing was lost.
        await Assert.That(server.Requests.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_watch_frame_over_the_cap_ends_the_stream_with_a_stated_error_and_a_relist()
    {
        var reported = new List<Exception>();
        var watches = 0;
        await using var server = new ConcurrentStubServer(async (request, response) =>
        {
            if (!request.IsWatch)
            {
                await response.WriteAsync(List(App("listed", 1)));
                return;
            }

            if (Interlocked.Increment(ref watches) == 1)
            {
                // A frame that never ends within the cap: 33 MiB of one line.
                await response.StreamAsync(async write =>
                {
                    await write("{\"type\":\"ADDED\",\"object\":{\"kind\":\"Application\",\"metadata\":{\"name\":\"huge\"},\"x\":\"");
                    var chunk = new string('x', 1024 * 1024);
                    for (var i = 0; i < 33; i++)
                    {
                        await write(chunk);
                    }
                });
                return;
            }

            await response.StreamAsync(Frame("ADDED", App("after", 1, "12")));
        });

        using var client = server.Connect();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var events = new List<string>();
        await foreach (var evt in client.WatchResourceAsync(
                           Applications, connectionLost: ex => { lock (reported) { reported.Add(ex); } },
                           cancellationToken: cts.Token))
        {
            events.Add(evt.Resource?.Name ?? evt.Type.ToString());
            if (evt.Resource?.Name == "after")
            {
                break;
            }
        }

        // Listed, then the oversized frame ended the watch, then a relist (a second Reset) and
        // the next watch's frame.
        await Assert.That(string.Join(",", events)).IsEqualTo("Reset,listed,Synced,Reset,listed,Synced,after");
        await Assert.That(reported.Count).IsEqualTo(1);
        await Assert.That(reported[0]).IsTypeOf<UnreadableObjectException>();
        await Assert.That(reported[0].Message).Contains("larger than 32 MiB (Application huge)");
    }

    [Test]
    public async Task A_log_line_over_the_cap_is_cut_with_a_marker_and_the_next_line_is_whole()
    {
        await using var server = new ConcurrentStubServer(async (_, response) =>
        {
            await response.StreamAsync(async write =>
            {
                await write("first\n");
                await write(new string('y', ClusterClient.MaxLogLineBytes + 10) + "tail\n");
                await write("after\n");
            });
        });

        using var client = server.Connect();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var lines = new List<string>();
        await foreach (var line in client.StreamPodLogsAsync("shop", "web-1", cancellationToken: cts.Token))
        {
            lines.Add(line);
            if (line == "after")
            {
                break;
            }
        }

        await Assert.That(lines.Count).IsEqualTo(3);
        await Assert.That(lines[0]).IsEqualTo("first");
        await Assert.That(lines[1].Length).IsEqualTo(ClusterClient.MaxLogLineBytes + ClusterClient.TruncatedLogLineMarker.Length);
        await Assert.That(lines[1]).EndsWith(ClusterClient.TruncatedLogLineMarker);
        await Assert.That(lines[1]).DoesNotContain("tail");
        await Assert.That(lines[2]).IsEqualTo("after");
    }

    [Test]
    public async Task A_cut_inside_a_multibyte_character_backs_up_to_the_last_whole_one()
    {
        var head = Encoding.UTF8.GetBytes("ab€");
        var text = ClusterClient.TruncatedLogLine(head.AsSpan(0, head.Length - 1));

        await Assert.That(text).IsEqualTo("ab" + ClusterClient.TruncatedLogLineMarker);
    }
}
