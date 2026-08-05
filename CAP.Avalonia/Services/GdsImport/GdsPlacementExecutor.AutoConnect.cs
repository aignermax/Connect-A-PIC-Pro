using System.Globalization;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Components.PinKinds;

namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// The experimental auto-connect stage of <see cref="GdsPlacementExecutor"/>
/// (issue #808 follow-up), split out to keep the executor under the project's
/// 500-line file-size gate.
/// </summary>
public sealed partial class GdsPlacementExecutor
{
    /// <summary>
    /// Experimental auto-connect (issue #808 follow-up): pairs the still-unoccupied
    /// optical pins of the placed instances via <see cref="GdsFreePinPairer"/> and
    /// connects each pair. Occupancy is checked against the canvas connection state
    /// (<see cref="DesignCanvasViewModel.GetConnectionForPin"/>) because
    /// <c>ConnectPinsAsync</c> REPLACES existing pin connections — only pins with no
    /// connection at all are candidates. All pairs are added deferred
    /// (<see cref="DesignCanvasViewModel.ConnectPins"/>) and routed in ONE
    /// recalculation at the end instead of a per-pair re-route storm.
    /// </summary>
    private async Task AutoConnectFreePinsAsync(
        GdsPlacementPlan plan,
        IReadOnlyList<ComponentViewModel?> placedViewModels,
        List<WaveguideConnection> createdConnections,
        GdsPlacementReport report,
        IProgress<string>? progress,
        double radiusUm,
        CancellationToken ct)
    {
        progress?.Report("Auto-connecting free pins…");

        var candidates = new List<GdsFreePinCandidate>();
        var candidatePins = new List<PhysicalPin>();
        for (var i = 0; i < placedViewModels.Count; i++)
        {
            var vm = placedViewModels[i];
            if (vm is null) continue;

            // Ordinal pin-name order keeps the pairing deterministic across runs.
            foreach (var pin in vm.Component.PhysicalPins.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var label = $"'{plan.Placements[i].InstanceName}.{pin.Name}'";
                if (PinKindHelper.IsElectrical(pin))
                {
                    report.SkippedAutoConnect.Add($"{label}: non-optical (electrical) pin.");
                    continue;
                }
                if (_canvas.GetConnectionForPin(pin) is not null)
                    continue; // occupied by an abutment connection — not a free pin.

                var (x, y) = pin.GetAbsolutePosition();
                candidates.Add(new GdsFreePinCandidate(label, x, y, pin.GetAbsoluteAngle(), i));
                candidatePins.Add(pin);
            }
        }

        var pairing = GdsFreePinPairer.Pair(candidates, radiusUm);

        // Same-reason skips beyond the detail cap collapse into one summary line —
        // a big import would otherwise flood the report with one "no opposing free
        // pin" line per external port. GroupBy keeps first-occurrence order, so the
        // report stays deterministic.
        foreach (var reasonGroup in pairing.Skipped.GroupBy(s => s.Reason))
        {
            var skips = reasonGroup.ToList();
            if (skips.Count > MaxDetailedAutoConnectSkips)
            {
                report.SkippedAutoConnect.Add(
                    FormatAutoConnectSkipSummary(reasonGroup.Key, skips.Count, radiusUm));
                continue;
            }
            foreach (var skip in skips)
                report.SkippedAutoConnect.Add(FormatAutoConnectSkip(candidates[skip.Index].Label, skip, radiusUm));
        }

        foreach (var pair in pairing.Pairs)
        {
            ct.ThrowIfCancellationRequested();
            var connectionVm = _canvas.ConnectPins(candidatePins[pair.A], candidatePins[pair.B]);
            if (connectionVm is not null)
            {
                createdConnections.Add(connectionVm.Connection);
                report.AutoConnectedCount++;
                report.AutoConnectedPairs.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{candidates[pair.A].Label} ↔ {candidates[pair.B].Label} ({pair.DistanceUm:0.#} µm)"));
            }
        }

        if (pairing.Pairs.Count > 0)
            await _canvas.RecalculateRoutesAsync(); // one routing pass for the whole batch
    }

    /// <summary>
    /// Cap of per-pin auto-connect skip detail lines: when more skips than this
    /// share one reason, they collapse into a single summary line (count + reason)
    /// instead of flooding the report.
    /// </summary>
    private const int MaxDetailedAutoConnectSkips = 5;

    private static string FormatAutoConnectSkip(string label, GdsFreePinSkip skip, double radiusUm) =>
        skip.Reason switch
        {
            GdsFreePinSkipReason.NotFacingEachOther => string.Format(CultureInfo.InvariantCulture,
                LocalizationService.Instance.Translate("GdsImport.AutoConnectSkipNotFacingFormat"), label, radiusUm),
            GdsFreePinSkipReason.AmbiguousNearestPartner => string.Format(CultureInfo.InvariantCulture,
                LocalizationService.Instance.Translate("GdsImport.AutoConnectSkipAmbiguousFormat"),
                label, skip.NearestDistanceUm, skip.SecondNearestDistanceUm),
            _ => string.Format(CultureInfo.InvariantCulture,
                LocalizationService.Instance.Translate("GdsImport.AutoConnectSkipNoPartnerFormat"), label, radiusUm),
        };

    /// <summary>
    /// One summary line for a same-reason skip flood (see
    /// <see cref="MaxDetailedAutoConnectSkips"/>): count plus reason, no per-pin labels.
    /// </summary>
    private static string FormatAutoConnectSkipSummary(GdsFreePinSkipReason reason, int count, double radiusUm) =>
        reason switch
        {
            GdsFreePinSkipReason.NotFacingEachOther => string.Format(CultureInfo.InvariantCulture,
                LocalizationService.Instance.Translate("GdsImport.AutoConnectSkipSummaryNotFacingFormat"), count),
            GdsFreePinSkipReason.AmbiguousNearestPartner => string.Format(CultureInfo.InvariantCulture,
                LocalizationService.Instance.Translate("GdsImport.AutoConnectSkipSummaryAmbiguousFormat"), count),
            _ => string.Format(CultureInfo.InvariantCulture,
                LocalizationService.Instance.Translate("GdsImport.AutoConnectSkipSummaryNoPartnerFormat"), count, radiusUm),
        };
}
