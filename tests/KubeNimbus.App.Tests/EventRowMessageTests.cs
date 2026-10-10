using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// ENG-49: the Events tabs (pod, node, workload, Service, Ingress detail) print an event's
/// message the way the Events list and <c>kubectl get events</c> do — trimmed. The kubelet
/// ends a probe's output with a newline, which drew a blank line under every probe event.
/// </summary>
public class EventRowMessageTests
{
    private static DynamicResource Event(string message)
    {
        using var document = JsonDocument.Parse($$$"""
            {"apiVersion":"v1","kind":"Event","type":"Warning","reason":"Unhealthy","count":3,
             "message":"{{{JsonEncodedText.Encode(message)}}}",
             "metadata":{"name":"web-1.17f","namespace":"shop"},
             "involvedObject":{"kind":"Pod","name":"web-1","namespace":"shop"}}
            """);
        return new DynamicResource(document.RootElement.Clone());
    }

    [Test]
    public async Task A_probe_message_loses_its_trailing_newline_as_the_list_trims_it()
    {
        var resource = Event("Readiness probe failed: HTTP probe failed with statuscode: 503\n");
        var pane = new EventRowViewModel(resource);
        var row = new ResourceRowViewModel(resource);

        await Assert.That(pane.Message).IsEqualTo("Readiness probe failed: HTTP probe failed with statuscode: 503");
        await Assert.That(pane.Message).IsEqualTo(row.EventMessageTooltip);
    }

    [Test]
    public async Task Line_breaks_inside_the_message_are_kept_for_the_panes_that_wrap()
    {
        var pane = new EventRowViewModel(Event("  Liveness probe failed: line one\nline two\n\n"));

        await Assert.That(pane.Message).IsEqualTo("Liveness probe failed: line one\nline two");
    }
}
