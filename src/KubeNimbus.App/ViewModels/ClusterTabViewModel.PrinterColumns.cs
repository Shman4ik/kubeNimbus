using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using KubeNimbus.Core;

namespace KubeNimbus.App.ViewModels;

// The columns a CRD declares for a list of its objects (additionalPrinterColumns), read for the
// selected kind and pushed to every row. Built-in kinds have none; see crd-printer-columns.md.
public sealed partial class ClusterTabViewModel
{
    // ------------------------------------------------- CRD printer columns
    //
    // A CustomResourceDefinition declares the columns it wants a list of its objects to
    // have, and kubectl honours them — `kubectl get certificates` prints cert-manager's
    // READY / SECRET / ISSUER, not a generic status. This app printed the same generic
    // Status column for all ~70 CRD kinds on a real cluster, which is the weakest
    // surface in a client that sells CRDs as first-class. Built-in kinds are untouched:
    // they are not CRDs, so there is nothing to read for them and ResourceStatusSummary
    // still owns every column they show.

    /// <summary>
    /// Everything the selected kind's CRD declares, unfiltered — the advanced-view and
    /// width decisions are made by <see cref="VisiblePrinterColumns"/>, so that a
    /// display switch never has to refetch. Empty for a built-in kind, for an
    /// aggregated API, for a CRD that declares nothing, and for a user who cannot read
    /// <c>apiextensions.k8s.io</c>; all four then render exactly the list they did
    /// before this existed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisiblePrinterColumns))]
    private IReadOnlyList<PrinterColumn> _printerColumns = [];

    /// <summary>
    /// The columns the grid actually draws: every column the CRD declares, priority-1
    /// included, with a declared Age folded into the list's own live Age column and the
    /// whole thing capped at the number of printer slots the grid declares. See
    /// <see cref="PrinterColumns.Visible"/>.
    ///
    /// <para>
    /// The priority-1 half used to ride the advanced view as this app's <c>-o wide</c>.
    /// That switch is the sidebar's alone now, and a column a CRD's author declared is
    /// exactly the content-area detail it may no longer withhold; a list wider than the
    /// window is the reader's to re-cut, since every column is draggable (FEAT-66).
    /// </para>
    /// </summary>
    public IReadOnlyList<PrinterColumn> VisiblePrinterColumns =>
        // Every declared column, priority included. The advanced view used to act as
        // kubectl's `-o wide` here; it governs the sidebar only now, and a column the
        // CRD's author declared is exactly the kind of content-area detail this switch
        // is no longer allowed to withhold. A list wider than the window is the reader's
        // to re-cut — every column is draggable (FEAT-66).
        KubeNimbus.Core.PrinterColumns.Visible(PrinterColumns, includeLowPriority: true, ResourceRowViewModel.PrinterCellCount);

    partial void OnPrinterColumnsChanged(IReadOnlyList<PrinterColumn> value) => PushPrinterColumnsToRows();

    /// <summary>
    /// Per-kind cache, for this tab's lifetime. Keyed by group/version/kind because a
    /// CRD serves different columns per version, and a cluster can be upgraded under a
    /// running tab. The negative answer is cached too — a built-in kind must not cost a
    /// 404 every time it is reselected.
    /// </summary>
    private readonly Dictionary<string, IReadOnlyList<PrinterColumn>> _printerColumnCache = new(StringComparer.Ordinal);

    private static string PrinterCacheKey(ResourceDescriptor descriptor) =>
        $"{descriptor.Group}/{descriptor.Version}/{descriptor.Kind}";

    /// <summary>
    /// Points the list at whatever printer columns the newly-selected kind has. The
    /// cached (or demo) answer lands synchronously so the grid is never briefly wrong;
    /// a cache miss on a live cluster clears the columns first and fills them in when
    /// the GET returns, which is one small request the first time a kind is opened.
    /// </summary>
    private void UpdatePrinterColumns(ResourceDescriptor descriptor)
    {
        if (Client is null)
        {
            // Demo cluster (or a tab with no client at all): the dataset answers for
            // its own CRD, through the same PrinterColumns.Parse a live cluster uses.
            PrinterColumns = IsDemo ? Demo.DemoData.PrinterColumnsFor(descriptor) : [];
            return;
        }

        var key = PrinterCacheKey(descriptor);
        if (_printerColumnCache.TryGetValue(key, out var cached))
        {
            PrinterColumns = cached;
            return;
        }

        PrinterColumns = [];

        var client = Client;
        var token = _watchCts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                var columns = await client.GetPrinterColumnsAsync(descriptor, token).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _printerColumnCache[key] = columns;

                    // The user can have moved on while this was in flight; the columns
                    // belong to the kind that asked for them and to no other.
                    if (SelectedKind?.Descriptor is { } current && PrinterCacheKey(current) == key)
                    {
                        PrinterColumns = columns;
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Normal: the kind or namespace changed while this was in flight. Not
                // cached either — the next selection of this kind should ask again.
            }
        }, token);
    }

    /// <summary>
    /// Hands the current column set to every row. Rows are created one at a time by the
    /// watch, so they take the set on creation too; this is for the two moments the set
    /// itself changes — the fetch landing, and the advanced-view switch.
    /// </summary>
    private void PushPrinterColumnsToRows()
    {
        var columns = VisiblePrinterColumns;
        foreach (var row in Rows)
        {
            row.SetPrinterColumns(columns);
        }

        // A sort by one of those columns has to be re-run against the new set: the
        // cells it orders by have just been re-evaluated, and the column may have
        // stopped being declared at all — in which case the list falls back to arrival
        // order rather than pretending to be sorted by something that is not there.
        if (SortColumnId is { } columnId && ResourceColumn.PrinterName(columnId) is not null)
        {
            RebuildVisibleRows();
        }
    }
}
