using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>One event row for the events panel — plain properties for simple compiled bindings.</summary>
public sealed class EventRowViewModel(DynamicResource e)
{
    public string Type { get; } = e.Type();

    public string Reason { get; } = InvisibleCharacters.Reveal(e.Reason());

    /// <summary>
    /// The event's message, trimmed at both ends as the Events list trims it (and as
    /// <c>kubectl get events</c> prints it): the kubelet ends a probe's output with a newline,
    /// which drew a blank line under every probe event in the panes that wrap it (ENG-49).
    /// Line breaks inside the message are kept — the panes wrap, and an exec probe's output
    /// can be several lines; the list folds them only because its cell is one line.
    /// </summary>
    public string Message { get; } = InvisibleCharacters.Reveal(e.Message().Trim());

    public int Count { get; } = e.Count();

    public DateTimeOffset? LastSeen { get; } = e.LastTimestamp();

    /// <summary>Warning/Normal → warn/ok, for the same statusPill/statusDot visual the resource list uses.</summary>
    public string Health { get; } = string.Equals(e.Type(), "Warning", StringComparison.OrdinalIgnoreCase) ? "warn" : "ok";

    public OwnerRef? InvolvedObject { get; } = e.InvolvedObject();

    public string? InvolvedObjectNamespace { get; } = e.InvolvedObjectNamespace();

    public bool HasInvolvedObject => InvolvedObject is not null;
}
