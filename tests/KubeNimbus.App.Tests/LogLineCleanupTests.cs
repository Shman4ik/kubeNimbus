using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// What a log line looks like once it reaches the pane: terminal colour codes removed, .NET's
/// console-logger levels read as severities, and the pod column shown only when it tells two
/// pods apart. Found on a real ASP.NET pod, whose every line began with a box glyph and
/// <c>[40m[32minfo[39m[22m[49m</c> beside the same pod name.
/// </summary>
public class LogLineCleanupTests
{
    private const string Esc = "\u001b";

    private static readonly ResourceDescriptor DeploymentDescriptor =
        new("apps", "v1", "Deployment", "deployments", "deployment", Namespaced: true, ShortNames: ["deploy"], Categories: []);

    private static WorkloadLogsTabViewModel Pane()
    {
        TestObjects.RedirectStores();
        using var document = JsonDocument.Parse("""
            {
              "apiVersion": "apps/v1", "kind": "Deployment",
              "metadata": { "name": "api", "namespace": "nowhere" },
              "spec": { "selector": { "matchLabels": { "app": "api-nothing-matches-this" } } }
            }
            """);
        var workload = new DynamicResource(document.RootElement.Clone());
        return new WorkloadLogsTabViewModel(null, DeploymentDescriptor, workload, LabelSelector.ForPodsOf(workload)!);
    }

    [Test]
    public async Task Colour_codes_are_removed_from_what_is_shown_searched_and_copied()
    {
        var raw = $"2026-09-28T06:46:04.000Z 06:46:04 {Esc}[40m{Esc}[32minfo{Esc}[39m{Esc}[22m{Esc}[49m: Microsoft.Hosting.Lifetime[14] Now listening";
        var line = new LogLineViewModel(raw, showTimestamp: false);

        await Assert.That(line.Message).IsEqualTo("06:46:04 info: Microsoft.Hosting.Lifetime[14] Now listening");
        await Assert.That(line.RawLine).IsEqualTo("2026-09-28T06:46:04.000Z 06:46:04 info: Microsoft.Hosting.Lifetime[14] Now listening");
        await Assert.That(line.Timestamp).IsEqualTo("2026-09-28T06:46:04.000Z");
        await Assert.That(line.Severity).IsEqualTo(LogSeverity.Info);
        await Assert.That(line.Contains("info: Microsoft")).IsTrue();
    }

    [Test]
    public async Task Dotnet_console_levels_colour_the_line()
    {
        static LogSeverity Of(string message) => new LogLineViewModel($"2026-09-28T06:46:04.000Z {message}", false).Severity;

        await Assert.That(Of($"{Esc}[1m{Esc}[33mwarn{Esc}[39m{Esc}[22m: Overriding HTTP_PORTS")).IsEqualTo(LogSeverity.Warn);
        await Assert.That(Of($"{Esc}[41m{Esc}[30mfail{Esc}[39m{Esc}[22m{Esc}[49m: Request failed")).IsEqualTo(LogSeverity.Error);
        await Assert.That(Of("crit: Host terminated unexpectedly")).IsEqualTo(LogSeverity.Error);
        // Only the logger's own shape: the word in a sentence is not a level.
        await Assert.That(Of("tests fail when the cache is cold")).IsEqualTo(LogSeverity.None);
        await Assert.That(Of("critical path computed")).IsEqualTo(LogSeverity.None);
    }

    [Test]
    public async Task Strip_handles_every_escape_shape_and_leaves_plain_lines_alone()
    {
        const string plain = "GET /healthz 200\tin 3ms";
        await Assert.That(ReferenceEquals(TerminalEscapes.Strip(plain), plain)).IsTrue();

        // OSC 8 hyperlink, terminated by ST and by BEL.
        await Assert.That(TerminalEscapes.Strip($"see {Esc}]8;;https://x.dev{Esc}\\docs{Esc}]8;;{Esc}\\ now")).IsEqualTo("see docs now");
        await Assert.That(TerminalEscapes.Strip($"{Esc}]0;title\u0007ready")).IsEqualTo("ready");
        // A charset designation (ESC ( B) and a two-byte escape.
        await Assert.That(TerminalEscapes.Strip($"{Esc}(Bplain{Esc}7")).IsEqualTo("plain");
        // A CSI with private parameters, and one cut off by the end of the line.
        await Assert.That(TerminalEscapes.Strip($"{Esc}[?25lspinner{Esc}[38;5")).IsEqualTo("spinner");
        // Other control characters would be drawn as boxes; tab is kept.
        await Assert.That(TerminalEscapes.Strip("a\u0008b\rc\td")).IsEqualTo("abc\td");
    }

    [Test]
    public async Task The_pod_column_is_shown_only_while_more_than_one_pod_is()
    {
        var pane = Pane();
        var a = pane.RegisterSource("api-a", "app");
        await Assert.That(pane.ShowSourceColumn).IsFalse();

        var b = pane.RegisterSource("api-b", "app");
        await Assert.That(pane.ShowSourceColumn).IsTrue();

        b.IsIncluded = false;
        await Assert.That(pane.ShowSourceColumn).IsFalse();

        b.IsIncluded = true;
        await Assert.That(pane.ShowSourceColumn).IsTrue();

        // Copy keeps the prefix whatever the column shows.
        pane.Enqueue("2026-09-28T06:46:04.000Z hello", a);
        pane.Flush(force: true);
        await Assert.That(pane.LogLines.Single().Source).IsSameReferenceAs(a);
    }
}
