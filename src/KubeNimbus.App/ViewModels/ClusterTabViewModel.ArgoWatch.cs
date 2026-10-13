using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

/// <summary>
/// The Argo CD dashboard on one cluster is a list+watch (#303), not the one-shot read it was:
/// a sync started with <c>kubectl</c> or Argo's own UI, a health change or a new Application
/// shows without a reload, which is the moment somebody is looking at it. It rides the list's
/// own <c>_watchCts</c>, so choosing another kind, Refresh and closing the tab stop it exactly as
/// they stop any list watch, and it reaches the UI thread in batches (<see cref="AsyncBatching"/>).
/// It reads the shared watch (<see cref="WatchCache"/>), which the Applications mode's Argo
/// watch also reads, and Refresh relists that entry.
/// <para>
/// The fleet list ("All clusters") is the same thing once per member (#336): one shared watch
/// per member cluster through <see cref="ClusterFleet.WatchAsync"/>, folded per cluster into an
/// <see cref="ArgoFleetTable"/>, so each row keeps its own cluster's descriptor and a member that
/// leaves the fleet stops being read when the fleet change restarts the watch. Only the demo
/// cluster is still a read; see <c>ReloadArgoAsync</c>.
/// </para>
/// <para>
/// A 403 ends the wait with a stated refusal (#339): <see cref="WatchCache"/> ends a refused
/// entry with a <see cref="WatchRefusedException"/> instead of retrying it, and the dashboard says
/// "not permitted" with the server's sentence instead of "connection lost" over a spinner. It
/// does not ask again until Refresh, a reconnect or the kind is chosen again.
/// </para>
/// </summary>
public sealed partial class ClusterTabViewModel
{
    /// <summary>
    /// The server's sentence when this user may not read Argo CD Applications here (a 403 on the
    /// dashboard's watch, #339), or null. Its own state in the dashboard's place, never a
    /// "connection lost" warning: a refusal is an answer.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArgoRefused))]
    private string? _argoRefusal;

    public bool IsArgoRefused => ArgoRefusal is not null;

    // The fleet list's watched members, for Refresh to relist.
    private IReadOnlyList<FleetTarget> _argoFleetTargets = [];

    // The row selected before the watch restarted, for its first rows to select again.
    private (string ClusterName, string Key)? _argoReselect;

    /// <summary>
    /// Starts the dashboard's watch on this tab's own cluster. Loading ends at the first row or at
    /// Synced, never at Reset (UI rule 18); a Reset mid-stream (a relist after 410 Gone) keeps the
    /// rows on screen until the relist has synced (<see cref="ArgoApplicationTable.IsRelisting"/>).
    /// </summary>
    private void StartArgoWatch(ClusterClient client, ResourceDescriptor descriptor)
    {
        var token = BeginArgoWatch();
        var table = new ArgoApplicationTable();

        _ = Task.Run(async () =>
        {
            try
            {
                // The shared watch (WatchCache): the Applications mode watches the same
                // collection, so the dashboard joins it rather than opening a second one.
                await foreach (var batch in client.Watches.WatchAsync(
                    descriptor,
                    connectionLost: ex => Dispatcher.UIThread.Post(() =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        ConnectionWarning = $"{ArgoReadWarning}: {ex.Message}";
                        ConnectionWarningOffersReconnect = ex is not UnreadableObjectException;
                    }),
                    cancellationToken: token).InBatches(cancellationToken: token))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            ApplyArgoBatch(table, batch, descriptor);
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Another kind, a refresh, the fleet toggle or the tab closing.
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => EndArgoWatch(ex, token));
            }
        }, token);
    }

    /// <summary>Stops whatever the list was watching and puts the dashboard into its loading state.</summary>
    private CancellationToken BeginArgoWatch()
    {
        StopWatch();
        _watchCts = new CancellationTokenSource();
        _argoFleetTargets = [];

        // A refresh must not move the selection: the new watch's first rows go back to it.
        _argoReselect = SelectedArgoApplication is { } selected ? (selected.ClusterName, selected.Application.Key) : null;
        IsArgoLoading = true;
        IsArgoEmpty = false;
        ArgoRefusal = null;
        ArgoApplications.Clear();
        ArgoCounts = null;
        OnPropertyChanged(nameof(ArgoAttentionSummary));
        return _watchCts.Token;
    }

    /// <summary>
    /// The dashboard's watch on this tab's cluster ended. A refusal is stated in the dashboard's
    /// place (<see cref="ArgoRefusal"/>); anything else is a warning over whatever rows there are.
    /// Either way the wait is over (UI rule 18).
    /// </summary>
    internal void EndArgoWatch(Exception ex, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        if (WatchRefusedException.From(ex) is { } refused)
        {
            ArgoRefusal = refused.Message;
            if (ConnectionWarning?.StartsWith(ArgoReadWarning, StringComparison.Ordinal) == true)
            {
                ConnectionWarning = null;
            }
        }
        else
        {
            ConnectionWarning = $"{ArgoReadWarning}: {ex.Message}";
        }

        IsArgoLoading = false;
        IsArgoEmpty = ArgoApplications.Count == 0 && ArgoRefusal is null;
    }

    /// <summary>Folds one batch of the dashboard's watch into the table and the grid.</summary>
    internal void ApplyArgoBatch(
        ArgoApplicationTable table, IReadOnlyList<ResourceEvent<DynamicResource>> batch, ResourceDescriptor descriptor)
    {
        foreach (var change in batch)
        {
            table.Apply(change);
        }

        if (!table.IsSynced && table.Count == 0)
        {
            // The list has started (or restarted) and has nothing yet: keep what is on screen.
            return;
        }

        if (ConnectionWarning?.StartsWith(ArgoReadWarning, StringComparison.Ordinal) == true)
        {
            ConnectionWarning = null;
        }

        var rows = table.Applications
            .Select(a => new ArgoApplicationRowViewModel(a, descriptor))
            .Order(ArgoSort.Comparer)
            .ToList();
        MergeArgoRows(rows);
        ArgoCounts = ArgoSummary.Of(table.Applications);
        IsArgoLoading = false;
        IsArgoEmpty = ArgoApplications.Count == 0;
        OnPropertyChanged(nameof(ArgoAttentionSummary));
    }

    /// <summary>
    /// Starts the fleet list's watch: each member resolves its own descriptor for the kind
    /// (<see cref="ClusterFleet.ResolveAsync"/>), then one shared watch per member that serves it.
    /// A member that cannot be resolved or whose watch ends is named in the warning and has
    /// answered; the list waits for the rest. The fleet change that removes a member restarts
    /// this, so a member that left is no longer read (<c>RefreshFleetMembership</c>).
    /// </summary>
    private void StartArgoFleetWatch(IReadOnlyList<FleetMember> members)
    {
        var token = BeginArgoWatch();
        var problems = new ArgoFleetProblems();

        _ = Task.Run(async () =>
        {
            try
            {
                var targets = await ClusterFleet.ResolveAsync(
                    members, ArgoCd.Group, "Application",
                    (member, ex) => Dispatcher.UIThread.Post(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            problems.Failed[member.ClusterName] = ex.Message;
                            ShowArgoFleetProblems(problems);
                        }
                    }),
                    token);
                var table = new ArgoFleetTable(targets);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    _argoFleetTargets = targets;
                    FleetSummary = $"{targets.Count} of {members.Count} clusters run Argo CD";
                    problems.Watched = targets.Count;
                    if (targets.Count == 0)
                    {
                        ArgoCounts = ArgoSummary.Of([]);
                        IsArgoLoading = false;
                        IsArgoEmpty = true;
                    }
                });

                if (targets.Count == 0)
                {
                    return;
                }

                await foreach (var batch in ClusterFleet.WatchAsync(
                    targets,
                    connectionLost: (member, ex) => Dispatcher.UIThread.Post(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            ConnectionWarning = $"{ArgoReadWarning} on {member.ClusterName}: {ex.Message}";
                            ConnectionWarningOffersReconnect = ex is not UnreadableObjectException;
                        }
                    }),
                    memberEnded: (member, ex) => Dispatcher.UIThread.Post(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            EndArgoFleetMember(table, problems, member.ClusterName, ex);
                        }
                    }),
                    cancellationToken: token).InBatches(cancellationToken: token))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            ApplyArgoFleetBatch(table, batch, problems);
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Another kind, a refresh, a fleet change or the tab closing.
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => EndArgoWatch(ex, token));
            }
        }, token);
    }

    /// <summary>What has gone wrong with the fleet list's members, for the one warning line.</summary>
    internal sealed class ArgoFleetProblems
    {
        /// <summary>How many members serve the kind and are watched.</summary>
        public int Watched { get; set; }

        /// <summary>Members that could not be resolved or whose watch failed, with the reason.</summary>
        public Dictionary<string, string> Failed { get; } = new(StringComparer.Ordinal);

        /// <summary>Members whose watch was refused (403), with the server's sentence.</summary>
        public Dictionary<string, string> Refused { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Folds one batch of the fleet watch into the per-cluster table and the grid.</summary>
    internal void ApplyArgoFleetBatch(ArgoFleetTable table, IReadOnlyList<FleetResourceEvent> batch, ArgoFleetProblems problems)
    {
        foreach (var change in batch)
        {
            table.Apply(change);
        }

        ShowArgoFleet(table, problems);
    }

    /// <summary>
    /// A member's watch ended for good: a refusal or a failure. It has answered, its rows leave
    /// the list, and it is named in the warning; when every member was refused, the refusal is
    /// the dashboard's state instead (#339).
    /// </summary>
    internal void EndArgoFleetMember(ArgoFleetTable table, ArgoFleetProblems problems, string clusterName, Exception ex)
    {
        table.MarkEnded(clusterName);
        if (WatchRefusedException.From(ex) is { } refused)
        {
            problems.Refused[clusterName] = refused.Message;
        }
        else
        {
            problems.Failed[clusterName] = ex.Message;
        }

        ShowArgoFleet(table, problems);
    }

    private void ShowArgoFleet(ArgoFleetTable table, ArgoFleetProblems problems)
    {
        var applications = table.Applications;
        if (applications.Count == 0 && !table.HasEveryAnswer)
        {
            // Nothing yet, and somebody has still to answer: keep waiting.
            ShowArgoFleetProblems(problems);
            return;
        }

        var rows = applications
            .Select(a => new ArgoApplicationRowViewModel(a.Application, a.Descriptor, a.ClusterName))
            .Order(ArgoSort.Comparer)
            .ToList();
        MergeArgoRows(rows);
        ArgoCounts = ArgoSummary.Of(applications.Select(a => a.Application));
        IsArgoLoading = false;
        ShowArgoFleetProblems(problems);
        IsArgoEmpty = ArgoApplications.Count == 0 && ArgoRefusal is null;
        OnPropertyChanged(nameof(ArgoAttentionSummary));
    }

    /// <summary>
    /// The fleet list's one warning line: the members that failed or were refused, the first
    /// reason. When every watched member was refused and none failed otherwise, that is the
    /// dashboard's refusal state rather than a warning.
    /// </summary>
    private void ShowArgoFleetProblems(ArgoFleetProblems problems)
    {
        var everyoneRefused = problems.Watched > 0 && problems.Refused.Count == problems.Watched && problems.Failed.Count == 0;
        ArgoRefusal = everyoneRefused ? problems.Refused.Values.First() : null;

        var named = problems.Failed.Concat(everyoneRefused ? [] : problems.Refused)
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToList();
        if (named.Count > 0)
        {
            ConnectionWarning = $"{ArgoReadWarning} on {string.Join(", ", named.Select(p => p.Key))}: {named[0].Value}";
            ConnectionWarningOffersReconnect = false;
        }
        else if (ConnectionWarning?.StartsWith(ArgoReadWarning, StringComparison.Ordinal) == true)
        {
            ConnectionWarning = null;
        }
    }

    /// <summary>
    /// Brings the grid to <paramref name="rows"/> touching as little as it can: when the same
    /// Applications are in the same order, only the rows whose object changed are replaced, so
    /// a status update every few seconds during a sync neither rebuilds the grid nor moves the
    /// reader's scroll position. Anything else (an Application added or deleted, an order
    /// change) is a rebuild. The selection stays on the same Application either way.
    /// </summary>
    private void MergeArgoRows(IReadOnlyList<ArgoApplicationRowViewModel> rows)
    {
        var selectedKey = SelectedArgoApplication is { } selected ? (selected.ClusterName, selected.Application.Key) : _argoReselect;
        _argoReselect = null;
        var sameOrder = rows.Count == ArgoApplications.Count
                        && rows.Select(r => (r.ClusterName, r.Application.Key))
                            .SequenceEqual(ArgoApplications.Select(r => (r.ClusterName, r.Application.Key)));
        if (sameOrder)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (!ReferenceEquals(ArgoApplications[i].Application, rows[i].Application))
                {
                    ArgoApplications[i] = rows[i];
                }
            }
        }
        else
        {
            ArgoApplications.Clear();
            foreach (var row in rows)
            {
                ArgoApplications.Add(row);
            }
        }

        // A watch event must not move the selection: somebody reading one Application's detail
        // has not asked to be moved to the worst one.
        SelectedArgoApplication =
            ArgoApplications.FirstOrDefault(a => (a.ClusterName, a.Application.Key) == selectedKey)
            ?? ArgoApplications.FirstOrDefault();
    }
}
