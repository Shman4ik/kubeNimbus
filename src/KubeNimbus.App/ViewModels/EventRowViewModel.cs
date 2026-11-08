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

    /// <summary>
    /// How many times the event happened, counted the way the Events list and kubectl count it
    /// (<see cref="EventFields.Occurrences"/>): the series count, else <c>count</c>, else
    /// <c>deprecatedCount</c>, and never less than one. The tabs used to read the raw
    /// <c>count</c> alone and printed "×0" beside an event the list said happened 23 times
    /// (the 2026-11-08 desktop run, batch P).
    /// </summary>
    public int Count { get; } = e.Occurrences();

    /// <summary>When the event last happened, by the list's own chain (<see cref="EventFields.LastSeen"/>).</summary>
    public DateTimeOffset? LastSeen { get; } = e.LastSeen();

    /// <summary>
    /// <see cref="LastSeen"/> in local time with its UTC offset, as the Events list's tooltip
    /// prints it (<see cref="LocalTimeText.Exact"/>), or "—" when the event carries no time.
    /// The tabs used to print the raw value, which is the API server's UTC.
    /// </summary>
    public string LastSeenText { get; } = e.LastSeen() is { } at ? LocalTimeText.Exact(at) : "—";

    /// <summary>Warning/Normal → warn/ok, for the same statusPill/statusDot visual the resource list uses.</summary>
    public string Health { get; } = string.Equals(e.Type(), "Warning", StringComparison.OrdinalIgnoreCase) ? "warn" : "ok";

    public OwnerRef? InvolvedObject { get; } = e.InvolvedObject();

    public string? InvolvedObjectNamespace { get; } = e.InvolvedObjectNamespace();

    public bool HasInvolvedObject => InvolvedObject is not null;
}
