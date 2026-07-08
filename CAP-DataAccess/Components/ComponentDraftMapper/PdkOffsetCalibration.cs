using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP_DataAccess.Components.ComponentDraftMapper;

/// <summary>Result of <see cref="PdkOffsetCalibration.ApplyAutoCalibrate"/>.</summary>
public enum AutoCalibrateOutcome
{
    /// <summary>Width / Height / Origin / pin offsets were updated.</summary>
    Success,
    /// <summary>Nazca render failed or no result was supplied — nothing to apply.</summary>
    NoPreview,
    /// <summary>Nazca bbox is zero-area; calibration would produce a degenerate box.</summary>
    DegenerateBbox,
    /// <summary>Lunima vs Nazca pin counts differ — auto-fix would have to invent or drop pins.</summary>
    PinCountMismatch,
}

/// <summary>Per-component verdict from <see cref="PdkOffsetCalibration.Evaluate"/>.</summary>
public enum ComponentCheckStatus
{
    /// <summary>Every Lunima pin is within the alignment tolerance of its matched Nazca pin.</summary>
    Aligned,
    /// <summary>Bbox / pin counts match but at least one pin is outside tolerance — fixable by Auto-Calibrate.</summary>
    Misaligned,
    /// <summary>Lunima and Nazca disagree on the pin count — manual edit required.</summary>
    PinCountMismatch,
    /// <summary>Nazca render returned no pins; alignment cannot be assessed.</summary>
    NoNazcaPins,
    /// <summary>The Python preview helper returned an error for this component.</summary>
    RenderFailed,
}

/// <summary>One row of the Check-All / Try-Fix-All report.</summary>
public record ComponentCheckResult(
    string ComponentName,
    ComponentCheckStatus Status,
    int LunimaPinCount,
    int NazcaPinCount,
    double WorstDeltaMicrometers,
    string Message)
{
    /// <summary>Coloured single-character glyph for table rows.</summary>
    public string StatusBadge => Status switch
    {
        ComponentCheckStatus.Aligned          => "✓",
        ComponentCheckStatus.Misaligned       => "⚠",
        ComponentCheckStatus.PinCountMismatch => "✗",
        ComponentCheckStatus.NoNazcaPins      => "?",
        ComponentCheckStatus.RenderFailed     => "✗",
        _                                     => "·",
    };

    /// <summary>True when Auto-Calibrate would resolve the issue without manual edits.</summary>
    public bool IsAutoFixable =>
        Status == ComponentCheckStatus.Misaligned ||
        Status == ComponentCheckStatus.Aligned;
}

/// <summary>
/// Pure calibration math shared by the single-component Auto-Calibrate command
/// and the Check-All / Try-Fix-All batch commands. No MVVM, no async, no UI —
/// keeps the ViewModel lean and lets the unit tests pin every formula.
/// </summary>
public static class PdkOffsetCalibration
{
    /// <summary>
    /// Greedy bipartite pin matcher: repeatedly takes the closest Lunima/Nazca
    /// pair (in current Lunima→Nazca-space projection) and removes both from
    /// the candidate sets. Returns one pair per Lunima pin assuming counts
    /// match, otherwise pairs up to <c>min(lunima, nazca)</c> pins.
    ///
    /// Cost = Euclidean distance + an angle-disagreement penalty so symmetric
    /// multi-port components (crossings, 2x2 MMIs, DCs) don't cross-pair when
    /// the pre-calibration projection lands roughly equidistant from each
    /// Nazca pin. The penalty is scaled by the component's bbox diagonal so
    /// it's commensurable with the Euclidean term: pins pointing in opposite
    /// directions (180° apart) are forbidden in practice. Pins on small
    /// components and pins on large components both behave correctly.
    /// </summary>
    public static List<(PhysicalPinDraft Lunima, NazcaPreviewPin Nazca)>
        MatchPinsByGreedyNearest(PdkComponentDraft draft, NazcaPreviewResult result)
    {
        var pairs = new List<(PhysicalPinDraft, NazcaPreviewPin)>();
        // Lunima→Nazca projection per the NazcaCoordinateMapper contract:
        // NazcaOriginOffsetY is the bbox TOP edge above the cell org (YMax),
        // so a pin's cell-local Nazca position is (OffsetX - ox, oy - OffsetY).
        var projections = draft.Pins
            .Select(lp => (
                lp,
                x: lp.OffsetXMicrometers - (draft.NazcaOriginOffsetX ?? 0),
                y: (draft.NazcaOriginOffsetY ?? 0) - lp.OffsetYMicrometers))
            .ToList();
        var availableNazca = result.Pins.ToList();

        // Bbox diagonal — used to scale the angle penalty so its contribution
        // is comparable to the Euclidean distance term. For a degenerate bbox
        // we fall back to 1 µm to keep the penalty meaningful.
        var bboxW = Math.Max(0.001, draft.WidthMicrometers);
        var bboxH = Math.Max(0.001, draft.HeightMicrometers);
        var diag  = Math.Sqrt(bboxW * bboxW + bboxH * bboxH);

        while (projections.Count > 0 && availableNazca.Count > 0)
        {
            var best = (lpIdx: -1, npIdx: -1, cost: double.MaxValue);
            for (var i = 0; i < projections.Count; i++)
            {
                for (var j = 0; j < availableNazca.Count; j++)
                {
                    var dx = availableNazca[j].X - projections[i].x;
                    var dy = availableNazca[j].Y - projections[i].y;
                    var d  = Math.Sqrt(dx * dx + dy * dy);
                    // Convert the Lunima (Y-down) angle into Nazca (Y-up) convention so
                    // the comparison stays inside one coordinate system. Without the
                    // flip a correctly-set Lunima angle of 90° would look 180° apart
                    // from its true Nazca match at 270°, and the matcher would
                    // cross-pair vertical pins on symmetric components.
                    var penalty = AngleDisagreementMicrometers(
                        FlipAngleConvention(projections[i].lp.AngleDegrees),
                        availableNazca[j].Angle, diag);
                    var cost = d + penalty;
                    if (cost < best.cost) best = (i, j, cost);
                }
            }
            pairs.Add((projections[best.lpIdx].lp, availableNazca[best.npIdx]));
            projections.RemoveAt(best.lpIdx);
            availableNazca.RemoveAt(best.npIdx);
        }
        return pairs;
    }

    /// <summary>
    /// Returns a distance-equivalent penalty for a pair whose declared angles
    /// disagree. 0° apart → 0 µm; 90° apart → ½·diag; 180° apart → diag.
    /// Scaling against the bbox diagonal keeps the penalty large enough to
    /// dominate position ties on symmetric components but small enough that
    /// it doesn't override a real positional match on asymmetric ones.
    /// Both inputs must be in the same convention (typically Nazca, since
    /// callers convert Lunima angles via <see cref="FlipAngleConvention"/>
    /// before passing them in).
    /// </summary>
    internal static double AngleDisagreementMicrometers(
        double angleDegreesA, double angleDegreesB, double bboxDiagonal)
    {
        var delta = Math.Abs(NormalizeAngle(angleDegreesA - angleDegreesB));
        if (delta > 180) delta = 360 - delta;  // wrap to [0, 180]
        return (delta / 180.0) * bboxDiagonal;
    }

    /// <summary>
    /// Maps a pin angle between the Nazca (Y-up, mathematical) and Lunima
    /// (Y-down, screen) conventions. The mapping is its own inverse:
    /// <c>lunima = (360 − nazca) mod 360</c>. Angles 0° and 180° are invariant
    /// under the flip; 90° and 270° swap. Used both when writing a Nazca pin
    /// angle into a Lunima draft and when comparing Lunima angles against
    /// Nazca ground truth.
    /// </summary>
    internal static double FlipAngleConvention(double angleDegrees)
    {
        return NormalizeAngle(360 - angleDegrees);
    }

    private static double NormalizeAngle(double a)
    {
        a %= 360;
        if (a < 0) a += 360;
        return a;
    }

    /// <summary>
    /// Mutates <paramref name="draft"/> so that Width / Height / NazcaOriginOffset
    /// match the Nazca bbox and every pin sits at its matched Nazca pin position.
    /// Returns the outcome enum so callers can decide how to surface failures.
    /// </summary>
    public static AutoCalibrateOutcome ApplyAutoCalibrate(
        PdkComponentDraft draft, NazcaPreviewResult result)
    {
        if (result is not { Success: true }) return AutoCalibrateOutcome.NoPreview;
        if (result.XMax <= result.XMin || result.YMax <= result.YMin)
            return AutoCalibrateOutcome.DegenerateBbox;
        if (result.Pins.Count != draft.Pins.Count)
            return AutoCalibrateOutcome.PinCountMismatch;

        var pairs = MatchPinsByGreedyNearest(draft, result);
        draft.WidthMicrometers   = result.XMax - result.XMin;
        draft.HeightMicrometers  = result.YMax - result.YMin;
        // Export contract (NazcaCoordinateMapper): the origin offset is the cell
        // org measured from the bbox TOP-LEFT corner — ox = -XMin, oy = YMax.
        // Writing -YMin here (the bottom edge) only coincides with YMax for
        // Y-symmetric cells and shifted asymmetric ones (demo.shallow.bend:
        // bbox Y ∈ [-9.4, 200]) by YMax + YMin ≈ 190.6 µm in the GDS (issue #635).
        draft.NazcaOriginOffsetX = -result.XMin;
        draft.NazcaOriginOffsetY = result.YMax;
        foreach (var (lp, np) in pairs)
        {
            lp.OffsetXMicrometers = np.X - result.XMin;
            lp.OffsetYMicrometers = result.YMax - np.Y;
            // Nazca pin angles live in Y-up math convention; Lunima renders in
            // Avalonia's Y-down screen space. The Y-flip swaps 90° and 270° while
            // leaving 0°/180° invariant — copying the raw Nazca angle into the
            // draft produced vertical pin stubs that pointed into the component
            // body instead of out of it (visible only after PR #539 made GDS
            // polygons render on the canvas).
            lp.AngleDegrees = FlipAngleConvention(np.Angle);
        }
        return AutoCalibrateOutcome.Success;
    }

    /// <summary>
    /// Inspects the alignment of <paramref name="draft"/>'s pins against
    /// <paramref name="result"/>'s Nazca pins under the current calibration
    /// and returns a verdict suitable for Check-All reports. Does NOT mutate
    /// the draft.
    /// </summary>
    public static ComponentCheckResult Evaluate(
        PdkComponentDraft draft, NazcaPreviewResult result, double toleranceMicrometers)
    {
        var name = draft.Name ?? "(unnamed)";
        if (result is not { Success: true })
            return new ComponentCheckResult(name, ComponentCheckStatus.RenderFailed,
                draft.Pins.Count, 0, double.NaN,
                result?.Error ?? "Render returned no result.");

        if (result.Pins.Count == 0)
            return new ComponentCheckResult(name, ComponentCheckStatus.NoNazcaPins,
                draft.Pins.Count, 0, double.NaN,
                "Nazca cell exposes no pins — alignment cannot be assessed.");

        if (result.Pins.Count != draft.Pins.Count)
            return new ComponentCheckResult(name, ComponentCheckStatus.PinCountMismatch,
                draft.Pins.Count, result.Pins.Count, double.NaN,
                $"Lunima declares {draft.Pins.Count} pins, Nazca exposes {result.Pins.Count}.");

        var pairs = MatchPinsByGreedyNearest(draft, result);
        double worst = 0;
        foreach (var (lp, np) in pairs)
        {
            // Same projection as MatchPinsByGreedyNearest: Lunima→Nazca-space.
            var x = lp.OffsetXMicrometers - (draft.NazcaOriginOffsetX ?? 0);
            var y = (draft.NazcaOriginOffsetY ?? 0) - lp.OffsetYMicrometers;
            var d = Math.Sqrt((np.X - x) * (np.X - x) + (np.Y - y) * (np.Y - y));
            if (d > worst) worst = d;
        }

        if (worst <= toleranceMicrometers)
            return new ComponentCheckResult(name, ComponentCheckStatus.Aligned,
                draft.Pins.Count, result.Pins.Count, worst,
                $"All {draft.Pins.Count} pins within {toleranceMicrometers:F1} µm.");

        return new ComponentCheckResult(name, ComponentCheckStatus.Misaligned,
            draft.Pins.Count, result.Pins.Count, worst,
            $"Worst pin delta {worst:F2} µm — Auto-Calibrate will fix.");
    }
}
