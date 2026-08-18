using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KubeNimbus.App.ViewModels;

/// <summary>What one pod's stream in an aggregated log pane is doing.</summary>
public enum LogSourceState
{
    /// <summary>The stream has been opened but no line has arrived yet.</summary>
    Starting,

    /// <summary>Lines are arriving.</summary>
    Streaming,

    /// <summary>The stream closed on its own — the container exited, or the pod went away.</summary>
    Ended,

    /// <summary>The stream could not be opened or died with an error the pane is showing.</summary>
    Failed,

    /// <summary>The pod was deleted while the pane was open; its lines are kept.</summary>
    Gone,
}

/// <summary>
/// One pod's contribution to an aggregated (multi-pod) log pane: its colour key, its
/// stream's state, and how many lines it has produced. Doubles as the pane's legend
/// and as its selector — the chip is a <c>ToggleButton</c> bound to
/// <see cref="IsIncluded"/> and to nothing else (UI rule 8b), so hiding one noisy
/// replica costs one click and no round trip.
/// </summary>
/// <remarks>
/// Keyed by pod <em>and</em> container even though FEAT-3 opens exactly one source per
/// pod. That is deliberate: the same pane tailing every container of one pod
/// (the separate FEAT-35 row) is then a change to which sources are created, not to
/// the merge, the buffer, the legend or the view.
/// </remarks>
public sealed partial class LogSourceViewModel : ObservableObject
{
    public string PodName { get; }

    public string ContainerName { get; }

    /// <summary>
    /// What the line prefix shows. Every pod of one workload shares the workload's name
    /// as a prefix, so printing it on every line spends the prefix column on the one
    /// part that cannot tell two pods apart — a 150px column of
    /// <c>payment-service-report-gener…</c> repeated down the pane. The remainder is
    /// the ReplicaSet hash and the pod suffix, which is exactly what does distinguish
    /// them (and what tells the old ReplicaSet from the new one mid-rollout).
    /// </summary>
    public string ShortName { get; }

    /// <summary>
    /// The colour this pod's lines are keyed with. A real <see cref="IBrush"/> held
    /// here rather than an index resolved by a converter, and never null: a binding
    /// that produces null writes a <em>local</em> null <c>Foreground</c>, which beats
    /// inheritance, and Avalonia's glyph-run draw early-returns on a null brush — that
    /// is how <c>LogSeverityToBrushConverter</c> once rendered most log lines
    /// completely invisible. There is no null to produce here.
    /// </summary>
    public IBrush Brush { get; }

    public LogSourceViewModel(string podName, string containerName, string shortName, IBrush brush)
    {
        PodName = podName;
        ContainerName = containerName;
        ShortName = shortName.Length == 0 ? podName : shortName;
        Brush = brush;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private LogSourceState _state = LogSourceState.Starting;

    /// <summary>Why the stream ended, when it ended for a reason worth stating.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private int _lineCount;

    /// <summary>
    /// Whether this pod's lines are shown. Bound two-way from a <c>ToggleButton</c>'s
    /// <c>IsChecked</c> with no command beside it (UI rule 8b); the pane listens for the
    /// change and re-applies its filter.
    /// </summary>
    [ObservableProperty]
    private bool _isIncluded = true;

    /// <summary>
    /// A short state word on the chip, and nothing at all while the stream is healthy —
    /// "Streaming" on every chip of a working pane is a label that only ever says the
    /// same thing.
    /// </summary>
    public string? StateLabel => State switch
    {
        LogSourceState.Starting => "connecting",
        LogSourceState.Ended => "ended",
        LogSourceState.Failed => "failed",
        LogSourceState.Gone => "deleted",
        _ => null,
    };

    public bool HasStateLabel => StateLabel is not null;

    public string Tooltip =>
        $"{PodName} · container {ContainerName}\n{LineCount:N0} line{(LineCount == 1 ? "" : "s")}"
        + (StatusMessage is { Length: > 0 } status ? $"\n{status}" : "");

    partial void OnStateChanged(LogSourceState value) => OnPropertyChanged(nameof(HasStateLabel));
}
