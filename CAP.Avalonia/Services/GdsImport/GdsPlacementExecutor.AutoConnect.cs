using System.Globalization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing.AutoConnect;

namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// The opt-in auto-connect stage of <see cref="GdsPlacementExecutor"/> (issue
/// #880, rung 1): after placement and plan-connection reconstruction, every
/// remaining unconnected pin is paired with its nearest facing partner
/// (<see cref="FacingPinPairFinder"/>) and routed with Lunima's own router —
/// direct/S-bend first, A* fallback, crossing insertion included, exactly like
/// an interactive pin-drag connect. Pairs route in small batches with a
/// cancellation check between batches (anytime semantics: a cancel keeps every
/// already-routed batch, nothing is rolled back). Pairs the router could not
/// route stay on the canvas as visible blocked (red) paths AND are named in the
/// report — never silently red.
/// </summary>
public sealed partial class GdsPlacementExecutor
{
    /// <summary>
    /// Pairs handed to the router per batch. Each batch ends in one incremental
    /// routing pass (already-valid routes are preserved), so the batch size sets
    /// the granularity of progress updates and cancellation checks.
    /// Internal set-seam for tests.
    /// </summary>
    internal int AutoConnectBatchSize { get; set; } = 20;

    /// <summary>
    /// Routes every facing pair of still-unconnected pins on the placed
    /// components; returns the created connections so the validation stage
    /// includes them. Respects frozen imported routes and already-placed
    /// components as obstacles (they are registered in the routing grid).
    /// </summary>
    private async Task<List<WaveguideConnection>> AutoConnectAllPinsAsync(
        IReadOnlyList<ComponentViewModel?> placedViewModels,
        GdsPlacementReport report,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var candidates = CollectUnconnectedPins(placedViewModels);
        var result = new FacingPinPairFinder().FindPairs(candidates);
        report.AutoConnectUnpairedPinCount = result.UnpairedPins.Count;

        var pairs = CapPairCount(result.Pairs, report);
        if (pairs.Count == 0)
            return new List<WaveguideConnection>();

        var created = await RoutePairsInBatchesAsync(pairs, progress, ct);
        report.AutoConnectedCount = created.Count;
        ReportUnroutablePairs(created, report);
        return created;
    }

    /// <summary>Creates and routes the pairs batch-wise; cancellation between batches keeps finished batches.</summary>
    private async Task<List<WaveguideConnection>> RoutePairsInBatchesAsync(
        IReadOnlyList<FacingPinPair> pairs,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var created = new List<WaveguideConnection>();
        var stageProgress = StageProgress(progress, "Auto-connecting pins");
        for (var batchStart = 0; batchStart < pairs.Count; batchStart += AutoConnectBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batchEnd = Math.Min(batchStart + AutoConnectBatchSize, pairs.Count);
            for (var i = batchStart; i < batchEnd; i++)
            {
                stageProgress?.Report(i + 1, pairs.Count);
                var connectionVm = _canvas.ConnectPins(pairs[i].A, pairs[i].B);
                if (connectionVm is not null)
                    created.Add(connectionVm.Connection);
            }
            // One incremental routing pass per batch: already-valid routes are
            // preserved, only this batch's new connections are routed — and a
            // cancel after this await keeps everything routed so far.
            await _canvas.RecalculateRoutesAsync();
        }
        return created;
    }

    /// <summary>
    /// All pins of the placed components that no connection touches yet (plan
    /// abutments and route-derived connections consumed theirs upstream).
    /// </summary>
    private List<FacingPinCandidate> CollectUnconnectedPins(
        IReadOnlyList<ComponentViewModel?> placedViewModels)
    {
        var connectedPins = new HashSet<PhysicalPin>();
        foreach (var connection in _canvas.Connections)
        {
            connectedPins.Add(connection.Connection.StartPin);
            connectedPins.Add(connection.Connection.EndPin);
        }

        var candidates = new List<FacingPinCandidate>();
        foreach (var viewModel in placedViewModels)
        {
            if (viewModel is null)
                continue;
            foreach (var pin in viewModel.Component.PhysicalPins)
            {
                if (!connectedPins.Contains(pin))
                    candidates.Add(FacingPinCandidate.FromPin(pin));
            }
        }
        return candidates;
    }

    /// <summary>
    /// Applies the <see cref="MaxReroutedConnections"/> cap that also guards the
    /// re-route stage: a failed route triggers full re-route attempts in several
    /// orderings, which at thousands of pairs turns the import into a hang.
    /// </summary>
    private static IReadOnlyList<FacingPinPair> CapPairCount(
        IReadOnlyList<FacingPinPair> pairs, GdsPlacementReport report)
    {
        if (pairs.Count <= MaxReroutedConnections)
            return pairs;
        report.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
            "Auto-connect was limited to the {0} nearest pin pairs — {1} more facing pairs " +
            "were left unconnected (limit guards against a minutes-long routing run).",
            MaxReroutedConnections, pairs.Count - MaxReroutedConnections));
        return pairs.Take(MaxReroutedConnections).ToList();
    }

    /// <summary>
    /// Names every auto-connected pair the router could not route. The blocked
    /// connections stay on the canvas (drawn red) so the user sees WHERE, and
    /// the report says so explicitly — grouped to one line per distinct message.
    /// </summary>
    private static void ReportUnroutablePairs(
        IReadOnlyList<WaveguideConnection> created, GdsPlacementReport report)
    {
        var grouper = new GdsReportLineGrouper();
        foreach (var connection in created)
        {
            if (connection.IsPathValid && !connection.IsBlockedFallback)
                continue;
            report.AutoConnectFailedCount++;
            grouper.Add(
                "auto-connect unroutable",
                $"Auto-connect could not route {DescribePin(connection.StartPin)} ↔ " +
                $"{DescribePin(connection.EndPin)} — kept as a blocked (red) path.");
        }
        grouper.FlushInto(report.Warnings);
    }

    private static string DescribePin(PhysicalPin pin) =>
        $"'{pin.ParentComponent?.Name}.{pin.Name}'";
}
