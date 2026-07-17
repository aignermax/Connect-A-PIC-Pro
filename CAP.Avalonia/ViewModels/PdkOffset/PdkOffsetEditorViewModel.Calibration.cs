using CAP.Avalonia.Services.Localization;
using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.PdkOffset;

/// <summary>
/// PdkOffsetEditorViewModel partial — pin-alignment, single-component
/// Auto-Calibrate, and the Check-All / Try-Fix-All batch commands.
/// All state (properties, fields) is declared in the main partial file.
/// </summary>
public partial class PdkOffsetEditorViewModel
{
    /// <summary>
    /// Compares Lunima's PDK-JSON pin positions against the Nazca render's
    /// pin stubs (in Nazca-space micrometres) and populates
    /// <see cref="PinAlignmentResults"/> + <see cref="PinAlignmentSummary"/>.
    /// Pin matching is delegated to <see cref="PdkOffsetCalibration.MatchPinsByGreedyNearest"/>
    /// so the inline overlay verdict and the Check-All / Try-Fix-All batch
    /// report (which goes through the same matcher) cannot disagree on the
    /// same component — name-matching is unreliable across PDKs (Lunima uses
    /// "in"/"out", SiEPIC uses "opt1"/"opt2").
    /// </summary>
    internal void ComputePinAlignment(NazcaPreviewResult result, PdkComponentDraft draft)
    {
        PinAlignmentResults.Clear();
        if (result.Pins.Count == 0 || draft.Pins.Count == 0)
        {
            PinAlignmentSummary = LocalizationService.Instance.Translate(result.Pins.Count == 0
                ? "PdkOffset.PinAlign.NoNazcaPins"
                : "PdkOffset.PinAlign.NoLunimaPins");
            return;
        }

        if (result.Pins.Count != draft.Pins.Count)
        {
            PinAlignmentSummary = string.Format(
                LocalizationService.Instance.Translate("PdkOffset.PinAlign.CountMismatch"),
                draft.Pins.Count, result.Pins.Count);
            return;
        }

        var pairs = PdkOffsetCalibration.MatchPinsByGreedyNearest(draft, result);
        int aligned = 0;
        foreach (var (lp, np) in pairs)
        {
            // Same projection as the matcher — see MatchPinsByGreedyNearest
            // for the convention (NazcaOriginOffsetY = bbox top above the org,
            // so cell-local Nazca y = oy - OffsetY; Lunima y-down vs Nazca y-up).
            var lunimaNazcaX = lp.OffsetXMicrometers - (draft.NazcaOriginOffsetX ?? 0);
            var lunimaNazcaY = (draft.NazcaOriginOffsetY ?? 0) - lp.OffsetYMicrometers;
            var dx = np.X - lunimaNazcaX;
            var dy = np.Y - lunimaNazcaY;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            var isAligned = dist <= PinAlignmentToleranceMicrometers;
            if (isAligned) aligned++;

            PinAlignmentResults.Add(new PinAlignmentInfo(
                lp.Name, np.Name, dx, dy, dist, isAligned));
        }

        PinAlignmentSummary = aligned == draft.Pins.Count
            ? string.Format(
                LocalizationService.Instance.Translate("PdkOffset.PinAlign.AllAligned"),
                aligned, draft.Pins.Count, PinAlignmentToleranceMicrometers)
            : string.Format(
                LocalizationService.Instance.Translate("PdkOffset.PinAlign.SomeAligned"),
                aligned, draft.Pins.Count, PinAlignmentResults.Max(p => p.DistanceMicrometers));
    }

    /// <summary>
    /// Derives Width / Height / NazcaOriginOffset from the cached Nazca bbox
    /// and snaps every Lunima pin to its matched Nazca pin position. The user
    /// no longer has to reverse-engineer the bbox math — one click and the
    /// JSON aligns with the GDS down to the pin.
    ///
    /// Pin matching is greedy bipartite by Euclidean distance using the
    /// component's CURRENT calibration as the projection space, so a
    /// roughly-correct starting offset is enough. Pin counts must match —
    /// otherwise the command refuses with an explicit error so the user
    /// knows the mismatch is real (e.g. SiEPIC GC has 'io' + 'wg' but the
    /// Lunima JSON only declares one pin).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAutoCalibrate))]
    private void AutoCalibrate()
    {
        if (_lastNazcaResult is not { Success: true } r || SelectedComponent == null)
        {
            StatusText = LocalizationService.Instance.Translate("PdkOffset.Status.AutoCalibNeedsPreview");
            return;
        }

        var draft = SelectedComponent.Draft;
        var outcome = PdkOffsetCalibration.ApplyAutoCalibrate(draft, r);
        if (outcome != AutoCalibrateOutcome.Success)
        {
            StatusText = outcome switch
            {
                AutoCalibrateOutcome.DegenerateBbox => string.Format(
                    LocalizationService.Instance.Translate("PdkOffset.Status.AutoCalibDegenerate"),
                    r.XMin, r.XMax, r.YMin, r.YMax),
                AutoCalibrateOutcome.PinCountMismatch => string.Format(
                    LocalizationService.Instance.Translate("PdkOffset.Status.AutoCalibPinMismatch"),
                    SelectedComponent.ComponentName, draft.Pins.Count, r.Pins.Count),
                _ => LocalizationService.Instance.Translate("PdkOffset.Status.AutoCalibUnknown"),
            };
            return;
        }

        // Mirror back into the bound numeric controls so the editor reflects
        // the new calibration without requiring the user to re-select the row.
        OffsetX = draft.NazcaOriginOffsetX!.Value;
        OffsetY = draft.NazcaOriginOffsetY!.Value;
        ComponentWidth = draft.WidthMicrometers;
        ComponentHeight = draft.HeightMicrometers;

        SelectedComponent.RefreshStatus();
        RefreshPinPositions(draft);
        RefreshCanvasMarkers(draft);
        ComputePinAlignment(r, draft);
        HasUnsavedChanges = true;
        StatusText = string.Format(
            LocalizationService.Instance.Translate("PdkOffset.Status.AutoCalibrated"),
            SelectedComponent.ComponentName, draft.WidthMicrometers, draft.HeightMicrometers,
            draft.NazcaOriginOffsetX, draft.NazcaOriginOffsetY);
    }

    private bool CanAutoCalibrate() =>
        _lastNazcaResult is { Success: true } && SelectedComponent != null;

    /// <summary>
    /// Test seam: lets unit tests place a synthetic <see cref="NazcaPreviewResult"/>
    /// into the cache slot the AutoCalibrate command reads from, without spinning
    /// up the Python preview pipeline.
    /// </summary>
    internal void SeedNazcaResultForTesting(NazcaPreviewResult result)
    {
        _lastNazcaResult = result;
        AutoCalibrateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Cancels any in-flight Check-All / Try-Fix-All run.</summary>
    [RelayCommand]
    private void CancelBatch() => _batchCts?.Cancel();

    /// <summary>
    /// Renders every PDK component through the Nazca preview helper and
    /// builds a per-component report (aligned / misaligned / pin-count
    /// mismatch / render-failed). Pure read-only — no draft mutation.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunBatch))]
    private async Task CheckAll() => await RunCheckAll();

    /// <summary>
    /// Runs Check-All, applies Auto-Calibrate to every fixable component,
    /// then re-evaluates each fixed component in-place against the same
    /// render result so the remaining report rows are exactly the
    /// components whose JSON / GDS combination cannot be auto-fixed
    /// (pin-count mismatch, render error). Avoids a second full Check-All
    /// pass — one Python render per component is enough to know the outcome.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunBatch))]
    private async Task TryFixAll()
    {
        if (_previewService == null || Components.Count == 0) return;

        _batchCts?.Cancel();
        _batchCts = new CancellationTokenSource();
        var token = _batchCts.Token;
        IsBatchRunning = true;
        try
        {
            await RunCheckAllInternal(token);
            if (token.IsCancellationRequested) return;

            int fixedCount = 0;
            // BatchCheckResults and Components share the same index order
            // because RunCheckAllInternal walks Components sequentially.
            for (int i = 0; i < Components.Count && i < BatchCheckResults.Count; i++)
            {
                if (token.IsCancellationRequested) break;
                if (BatchCheckResults[i].Status != ComponentCheckStatus.Misaligned) continue;
                var item = Components[i];
                BatchProgress = string.Format(
                    LocalizationService.Instance.Translate("PdkOffset.Batch.Fixing"), item.Draft.Name);
                var result = await RenderForBatch(item.Draft, token);
                if (result?.Success != true) continue;
                var outcome = PdkOffsetCalibration.ApplyAutoCalibrate(item.Draft, result);
                if (outcome != AutoCalibrateOutcome.Success) continue;

                int idx = i;
                fixedCount++;
                await UiThreadMarshaller(() =>
                {
                    item.RefreshStatus();
                    HasUnsavedChanges = true;
                    // Re-evaluate the same draft against the same render result.
                    // The post-fix Δmax should be 0 by construction since pins
                    // were just snapped to the Nazca positions. Replacing the
                    // row keeps the report and the underlying state coherent
                    // without paying for a full second Check-All pass.
                    BatchCheckResults[idx] = PdkOffsetCalibration.Evaluate(
                        item.Draft, result, PinAlignmentToleranceMicrometers);
                });
            }

            int total = BatchCheckResults.Count;
            int aligned = BatchCheckResults.Count(r => r.Status == ComponentCheckStatus.Aligned);
            int remaining = total - aligned;
            BatchSummary = remaining == 0
                ? string.Format(
                    LocalizationService.Instance.Translate("PdkOffset.Batch.TryFixAllDone"), fixedCount, total)
                : string.Format(
                    LocalizationService.Instance.Translate("PdkOffset.Batch.TryFixAllPartial"),
                    fixedCount, aligned, total, remaining);
            // Refresh the currently-selected component's overlay so the user
            // sees their fix without having to re-click the row.
            if (SelectedComponent != null)
                _ = TriggerNazcaRenderAsync(SelectedComponent.Draft);
        }
        finally
        {
            IsBatchRunning = false;
            BatchProgress = "";
        }
    }

    /// <summary>Copies the full batch report (markdown table) to the clipboard.</summary>
    [RelayCommand]
    private async Task CopyBatchReport()
    {
        if (CopyToClipboard == null || BatchCheckResults.Count == 0) return;
        await CopyToClipboard(FormatBatchReport(BatchCheckResults, errorsOnly: false));
        StatusText = string.Format(
            LocalizationService.Instance.Translate("PdkOffset.Status.CopiedReport"), BatchCheckResults.Count);
    }

    /// <summary>Copies only the rows that aren't fully aligned — the bits a human still has to investigate.</summary>
    [RelayCommand]
    private async Task CopyBatchErrors()
    {
        if (CopyToClipboard == null) return;
        var errors = BatchCheckResults
            .Where(r => r.Status != ComponentCheckStatus.Aligned)
            .ToList();
        if (errors.Count == 0)
        {
            StatusText = LocalizationService.Instance.Translate("PdkOffset.Status.NoErrorsToCopy");
            return;
        }
        await CopyToClipboard(FormatBatchReport(errors, errorsOnly: true));
        StatusText = string.Format(
            LocalizationService.Instance.Translate("PdkOffset.Status.CopiedErrors"), errors.Count);
    }

    /// <summary>
    /// Formats <paramref name="rows"/> as a markdown table. Designed to be
    /// pasted into a chat with Claude — the header line tells the assistant
    /// what kind of data follows, and the table is render-friendly.
    /// </summary>
    internal static string FormatBatchReport(
        IEnumerable<ComponentCheckResult> rows, bool errorsOnly)
    {
        var list = rows.ToList();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(errorsOnly
            ? $"PDK calibration — {list.Count} unresolved component(s)"
            : $"PDK calibration report — {list.Count} component(s)");
        sb.AppendLine();
        sb.AppendLine("| Component | Status | Pins L/N | Δmax (µm) | Message |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in list)
        {
            var delta = double.IsNaN(r.WorstDeltaMicrometers)
                ? "—"
                : r.WorstDeltaMicrometers.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            sb.AppendLine($"| {r.ComponentName} | {r.Status} | " +
                          $"{r.LunimaPinCount}/{r.NazcaPinCount} | {delta} | {r.Message} |");
        }
        return sb.ToString();
    }

    private async Task RunCheckAll()
    {
        if (_previewService == null || Components.Count == 0) return;
        _batchCts?.Cancel();
        _batchCts = new CancellationTokenSource();
        var token = _batchCts.Token;
        IsBatchRunning = true;
        try
        {
            await RunCheckAllInternal(token);
            int aligned = BatchCheckResults.Count(r => r.Status == ComponentCheckStatus.Aligned);
            int total = BatchCheckResults.Count;
            BatchSummary = aligned == total
                ? string.Format(
                    LocalizationService.Instance.Translate("PdkOffset.Batch.CheckAllDone"), total)
                : string.Format(
                    LocalizationService.Instance.Translate("PdkOffset.Batch.CheckAllPartial"),
                    aligned, total,
                    BatchCheckResults.Count(r => r.IsAutoFixable && r.Status != ComponentCheckStatus.Aligned));
        }
        finally
        {
            IsBatchRunning = false;
            BatchProgress = "";
        }
    }

    private async Task RunCheckAllInternal(CancellationToken token)
    {
        await UiThreadMarshaller(() => BatchCheckResults.Clear());
        for (int i = 0; i < Components.Count; i++)
        {
            if (token.IsCancellationRequested) return;
            var item = Components[i];
            BatchProgress = string.Format(
                LocalizationService.Instance.Translate("PdkOffset.Batch.Progress"),
                i + 1, Components.Count, item.Draft.Name);
            var result = await RenderForBatch(item.Draft, token);
            if (token.IsCancellationRequested) return;
            var check = PdkOffsetCalibration.Evaluate(
                item.Draft, result ?? NazcaPreviewResult.Fail("render returned null"),
                PinAlignmentToleranceMicrometers);
            await UiThreadMarshaller(() => BatchCheckResults.Add(check));
        }
    }

    internal async Task<NazcaPreviewResult?> RenderForBatch(PdkComponentDraft draft, CancellationToken token)
    {
        try
        {
            // Same render routing as the interactive path: gdsfactory-native components go through
            // the gdsfactory back-end, not the Nazca demo.() path — otherwise Check-All / Try-Fix-All
            // report every gdsfactory component as RenderFailed (#570).
            return await RenderDraftAsync(draft, token);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            return NazcaPreviewResult.Fail(ex.Message);
        }
    }

    private bool CanRunBatch() =>
        _previewService != null && !IsBatchRunning && Components.Count > 0;
}
