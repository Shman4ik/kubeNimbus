using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// Every port-forward running in the window, whichever tab started it (FEAT-7). Forwards
/// used to die with their dock tab, and nothing listed what was still listening on the
/// machine; now a running forward is listed here from Start to Stop, its tab can be closed
/// and reopened, and the status bar counts what is running.
/// </summary>
/// <remarks>
/// <para>
/// <b>One per window, and nothing on disk.</b> The shell owns it and hands it to every
/// cluster tab. A forward is a listening socket in this process, so it ends with the app,
/// and a list of forwards from the last session would be a history of sockets that no
/// longer exist.
/// </para>
/// <para>
/// <b>The pane's view model is the forward.</b> What is listed is the
/// <see cref="PortForwardTabViewModel"/> itself, so reopening a forward shows its live
/// state (the pod a Service forward reaches, the last refused connection) rather than a
/// summary of it.
/// </para>
/// <para>
/// <b>A forward belongs to its cluster tab's connection.</b> Closing the cluster tab
/// disposes the client every forward on it tunnels through, so
/// <see cref="StopForClusterAsync"/> stops them first and the status bar says how many it
/// stopped. A forward is not left listening on a dead client.
/// </para>
/// </remarks>
public sealed partial class PortForwardRegistry : ObservableObject
{
    public PortForwardRegistry()
    {
        Forwards.CollectionChanged += OnForwardsChanged;
    }

    /// <summary>Every running forward, in the order they were started.</summary>
    public ObservableCollection<PortForwardTabViewModel> Forwards { get; } = [];

    public int Count => Forwards.Count;

    public bool IsEmpty => Forwards.Count == 0;

    /// <summary>
    /// The last thing the registry did that nobody asked it to — forwards stopped because
    /// their cluster tab closed. Cleared by the next forward started or stopped, by opening
    /// the forwards list, and after <see cref="NoticeLifetime"/>, so the status bar it keeps
    /// open does not stay open for it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSomethingToShow))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string? _notice;

    /// <summary>Panes whose Start is still in flight — a Service forward resolves over the network before it binds.</summary>
    private readonly HashSet<PortForwardTabViewModel> _starting = [];

    internal static readonly TimeSpan NoticeLifetime = TimeSpan.FromSeconds(15);

    partial void OnNoticeChanged(string? value)
    {
        if (value is null)
        {
            return;
        }

        _ = Task.Delay(NoticeLifetime).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(Notice, value))
                {
                    Notice = null;
                }
            }),
            TaskScheduler.Default);
    }

    /// <summary>Whether the status bar has something of the registry's to show.</summary>
    public bool HasSomethingToShow => Count > 0 || Notice is not null;

    /// <summary>
    /// The status bar's line: the count of running forwards, and the last notice beside it
    /// while it lasts. The notice is shown with a count too: closing one cluster's tab while
    /// another cluster's forward runs is exactly when "1 port-forward" alone would hide that
    /// a forward was just stopped.
    /// </summary>
    public string StatusText
    {
        get
        {
            var count = Count switch
            {
                0 => "",
                1 => "1 port-forward",
                var n => $"{n} port-forwards",
            };
            return (count, Notice) switch
            {
                ({ Length: 0 }, var notice) => notice ?? "",
                (_, null) => count,
                (_, var notice) => $"{count} · {notice}",
            };
        }
    }

    /// <summary>One line per forward, then the notice — the status bar item's tooltip.</summary>
    public string Summary => string.Join(
        Environment.NewLine,
        Forwards.Select(f => $"{f.LocalAddress} → {f.TargetDescription} · {f.ClusterLabel}")
            .Append(Notice ?? (Count == 0 ? "No port-forward is running." : null))
            .OfType<string>());

    /// <summary>Brings a forward's cluster tab to the front; set by the shell.</summary>
    internal Action<ClusterTabViewModel>? SelectTab { get; set; }

    internal void Add(PortForwardTabViewModel forward)
    {
        Notice = null;
        if (!Forwards.Contains(forward))
        {
            Forwards.Add(forward);
        }
    }

    /// <summary>A pane's Start has begun; a cluster tab closing now cancels it (see <see cref="StopForClusterAsync"/>).</summary>
    internal void BeginStart(PortForwardTabViewModel forward) => _starting.Add(forward);

    internal void EndStart(PortForwardTabViewModel forward) => _starting.Remove(forward);

    internal void Remove(PortForwardTabViewModel forward)
    {
        Notice = null;
        Forwards.Remove(forward);
    }

    /// <summary>
    /// Shows a forward's pane again, in the cluster tab that started it — added back to its
    /// dock if its tab was closed, selected if it is still there.
    /// </summary>
    public void Reopen(PortForwardTabViewModel forward)
    {
        ArgumentNullException.ThrowIfNull(forward);
        if (forward.Owner is not { } owner)
        {
            return;
        }

        SelectTab?.Invoke(owner);
        owner.ShowForward(forward);
    }

    /// <summary>
    /// Stops every forward the cluster tab started, and every one that tunnels through its
    /// client (a fleet row's forward runs on its own cluster's tab), before that client is
    /// disposed. Returns how many it stopped, and states it in <see cref="Notice"/>.
    /// </summary>
    public async Task<int> StopForClusterAsync(ClusterTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        bool OnCluster(PortForwardTabViewModel f) =>
            ReferenceEquals(f.Owner, tab) || (tab.Client is { } client && ReferenceEquals(f.Client, client));

        var reason = $"Stopped: the {tab.Header} tab was closed.";

        // A Start still in flight (a Service forward resolving over the network) is not in
        // Forwards yet, and would otherwise bind and register after the client is disposed:
        // a local port that fails every connection, listed with no tab behind it.
        var cancelled = _starting.Where(OnCluster).ToList();
        foreach (var starting in cancelled)
        {
            starting.CancelStart(reason);
        }

        var doomed = Forwards.Where(OnCluster).ToList();
        foreach (var forward in doomed)
        {
            await forward.StopForwardingAsync(reason);
        }

        doomed.AddRange(cancelled);

        if (doomed.Count > 0)
        {
            Notice = doomed.Count == 1
                ? $"Stopped 1 port-forward on {tab.Header} — its tab was closed."
                : $"Stopped {doomed.Count} port-forwards on {tab.Header} — its tab was closed.";
        }

        return doomed.Count;
    }

    private void OnForwardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasSomethingToShow));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Summary));
    }
}
