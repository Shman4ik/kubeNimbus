using Avalonia.Threading;
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
/// The fleet list and the demo cluster stay reads; see <c>ReloadArgoAsync</c>.
/// </summary>
public sealed partial class ClusterTabViewModel
{
    /// <summary>
    /// Starts the dashboard's watch on this tab's own cluster. Loading ends at the first row or at
    /// Synced, never at Reset (UI rule 18); a Reset mid-stream (a relist after 410 Gone) keeps the
    /// rows on screen until the relist has something to show.
    /// </summary>
    private void StartArgoWatch(ClusterClient client, ResourceDescriptor descriptor)
    {
        StopWatch();
        _watchCts = new CancellationTokenSource();
        var token = _watchCts.Token;
        var table = new ArgoApplicationTable();

        IsArgoLoading = true;
        IsArgoEmpty = false;
        ArgoApplications.Clear();
        ArgoCounts = null;
        OnPropertyChanged(nameof(ArgoAttentionSummary));

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
                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    ConnectionWarning = $"{ArgoReadWarning}: {ex.Message}";
                    IsArgoLoading = false;
                    IsArgoEmpty = ArgoApplications.Count == 0;
                });
            }
        }, token);
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
    /// Brings the grid to <paramref name="rows"/> touching as little as it can: when the same
    /// Applications are in the same order, only the rows whose object changed are replaced, so
    /// a status update every few seconds during a sync neither rebuilds the grid nor moves the
    /// reader's scroll position. Anything else (an Application added or deleted, an order
    /// change) is a rebuild. The selection stays on the same Application either way.
    /// </summary>
    private void MergeArgoRows(IReadOnlyList<ArgoApplicationRowViewModel> rows)
    {
        var selectedKey = SelectedArgoApplication?.Application.Key;
        var sameOrder = rows.Count == ArgoApplications.Count
                        && rows.Select(r => r.Application.Key).SequenceEqual(ArgoApplications.Select(r => r.Application.Key));
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
            ArgoApplications.FirstOrDefault(a => a.Application.Key == selectedKey)
            ?? ArgoApplications.FirstOrDefault();
    }
}
