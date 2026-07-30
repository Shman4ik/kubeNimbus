using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>One event row for the events panel — plain properties for simple compiled bindings.</summary>
public sealed class EventRowViewModel(DynamicResource e)
{
    public string Type { get; } = e.Type();

    public string Reason { get; } = e.Reason();

    public string Message { get; } = e.Message();

    public int Count { get; } = e.Count();

    public DateTimeOffset? LastSeen { get; } = e.LastTimestamp();

    /// <summary>Warning/Normal → warn/ok, for the same statusPill/statusDot visual the resource list uses.</summary>
    public string Health { get; } = string.Equals(e.Type(), "Warning", StringComparison.OrdinalIgnoreCase) ? "warn" : "ok";

    public OwnerRef? InvolvedObject { get; } = e.InvolvedObject();

    public string? InvolvedObjectNamespace { get; } = e.InvolvedObjectNamespace();

    public bool HasInvolvedObject => InvolvedObject is not null;
}
