using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.Services;

/// <summary>
/// Severity level of a PDK consistency finding.
/// </summary>
public enum PdkFindingSeverity
{
    /// <summary>Informational — not a problem, just useful context.</summary>
    Info,

    /// <summary>Warning — may cause coordinate offsets; should be reviewed.</summary>
    Warning,

    /// <summary>Error — will cause incorrect GDS export; must be fixed.</summary>
    Error
}

/// <summary>
/// A single finding from a PDK consistency check.
/// Issue #334: PDK JSON vs Nazca Python coordinate mismatch investigation.
/// </summary>
public class PdkConsistencyFinding
{
    /// <summary>Name of the component this finding belongs to.</summary>
    public string ComponentName { get; init; } = "";

    /// <summary>Short type label for the finding (e.g., "PinOutOfBounds").</summary>
    public string FindingType { get; init; } = "";

    /// <summary>Human-readable description of the finding.</summary>
    public string Message { get; init; } = "";

    /// <summary>Severity of this finding.</summary>
    public PdkFindingSeverity Severity { get; init; }

    /// <summary>Optional deviation amount in µm when comparing positions.</summary>
    public double? DeviationMicrometers { get; init; }
}

/// <summary>
/// Validates PDK component definitions for coordinate consistency.
///
/// Issue #334: Investigates whether JSON PDK definitions match Nazca Python
/// implementations. Catches the root cause of the GDS export coordinate bug:
/// NazcaOriginOffset is derived from the first pin in JSON files, which may
/// not correspond to Nazca's actual a0 pin position.
/// </summary>
public class PdkConsistencyChecker
{
    /// <summary>Tolerance for pin bounds checking (µm).</summary>
    private const double BoundsTolerance = 1.0;

    /// <summary>Tolerance for dimension comparison against reference templates (µm).</summary>
    private const double DimensionTolerance = 0.5;

    /// <summary>Tolerance for pin position comparison against reference templates (µm).</summary>
    private const double PinPositionTolerance = 0.1;

    /// <summary>
    /// Checks all components in a PDK draft for internal consistency.
    /// Reports pins outside bounds, missing dimensions, and origin offset risks.
    /// </summary>
    public List<PdkConsistencyFinding> Check(PdkDraft pdkDraft)
    {
        var findings = new List<PdkConsistencyFinding>();

        foreach (var comp in pdkDraft.Components)
        {
            ValidateDimensions(comp, findings);
            ValidatePinBounds(comp, findings);
            CheckOriginOffsetRisk(comp, findings);
        }

        return findings;
    }

    /// <summary>
    /// Compares JSON PDK components against reference ComponentTemplates.
    /// Templates are matched by NazcaFunctionName. Unmatched components are skipped.
    /// </summary>
    public List<PdkConsistencyFinding> CompareWithTemplates(
        PdkDraft pdkDraft,
        IEnumerable<ComponentTemplate> referenceTemplates)
    {
        var findings = new List<PdkConsistencyFinding>();
        var byFunction = referenceTemplates
            .Where(t => t.NazcaFunctionName != null)
            .ToDictionary(t => t.NazcaFunctionName!, StringComparer.OrdinalIgnoreCase);

        foreach (var comp in pdkDraft.Components)
        {
            if (!byFunction.TryGetValue(comp.NazcaFunction ?? "", out var template))
                continue;

            CompareDimensions(comp, template, findings);
            ComparePins(comp, template, findings);
        }

        return findings;
    }

    private static void ValidateDimensions(PdkComponentDraft comp, List<PdkConsistencyFinding> findings)
    {
        if (comp.WidthMicrometers <= 0)
            findings.Add(new PdkConsistencyFinding
            {
                ComponentName = comp.Name,
                FindingType = "InvalidDimension",
                Message = string.Format(
                    LocalizationService.Instance.Translate("PdkConsistency.WidthNotPositive"),
                    comp.WidthMicrometers),
                Severity = PdkFindingSeverity.Error
            });

        if (comp.HeightMicrometers <= 0)
            findings.Add(new PdkConsistencyFinding
            {
                ComponentName = comp.Name,
                FindingType = "InvalidDimension",
                Message = string.Format(
                    LocalizationService.Instance.Translate("PdkConsistency.HeightNotPositive"),
                    comp.HeightMicrometers),
                Severity = PdkFindingSeverity.Error
            });
    }

    private static void ValidatePinBounds(PdkComponentDraft comp, List<PdkConsistencyFinding> findings)
    {
        foreach (var pin in comp.Pins)
        {
            var outX = pin.OffsetXMicrometers < -BoundsTolerance
                || pin.OffsetXMicrometers > comp.WidthMicrometers + BoundsTolerance;
            var outY = pin.OffsetYMicrometers < -BoundsTolerance
                || pin.OffsetYMicrometers > comp.HeightMicrometers + BoundsTolerance;

            if (outX || outY)
                findings.Add(new PdkConsistencyFinding
                {
                    ComponentName = comp.Name,
                    FindingType = "PinOutOfBounds",
                    Message = string.Format(
                        LocalizationService.Instance.Translate("PdkConsistency.PinOutOfBounds"),
                        pin.Name, pin.OffsetXMicrometers, pin.OffsetYMicrometers,
                        comp.WidthMicrometers, comp.HeightMicrometers),
                    Severity = PdkFindingSeverity.Warning
                });
        }
    }

    private static void CheckOriginOffsetRisk(PdkComponentDraft comp, List<PdkConsistencyFinding> findings)
    {
        // ConvertPdkComponentToTemplate() derives NazcaOriginOffset from the first pin.
        // If the first pin is not the Nazca a0/origin pin, this will be wrong.
        var firstPin = comp.Pins.FirstOrDefault();
        if (firstPin == null)
            return;

        // Known safe first-pin names that are typically the Nazca origin
        var safeOriginPins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "a0", "in", "waveguide", "opt", "west0" };

        if (!safeOriginPins.Contains(firstPin.Name))
            findings.Add(new PdkConsistencyFinding
            {
                ComponentName = comp.Name,
                FindingType = "OriginOffsetRisk",
                Message = string.Format(
                    LocalizationService.Instance.Translate("PdkConsistency.OriginOffsetRisk"),
                    firstPin.Name, firstPin.OffsetXMicrometers, firstPin.OffsetYMicrometers),
                Severity = PdkFindingSeverity.Warning
            });
    }

    private static void CompareDimensions(
        PdkComponentDraft comp, ComponentTemplate template, List<PdkConsistencyFinding> findings)
    {
        var widthDiff = Math.Abs(comp.WidthMicrometers - template.WidthMicrometers);
        var heightDiff = Math.Abs(comp.HeightMicrometers - template.HeightMicrometers);

        if (widthDiff > DimensionTolerance)
            findings.Add(new PdkConsistencyFinding
            {
                ComponentName = comp.Name,
                FindingType = "DimensionMismatch",
                Message = string.Format(
                    LocalizationService.Instance.Translate("PdkConsistency.WidthMismatch"),
                    comp.WidthMicrometers, template.WidthMicrometers, widthDiff),
                Severity = PdkFindingSeverity.Error,
                DeviationMicrometers = widthDiff
            });

        if (heightDiff > DimensionTolerance)
            findings.Add(new PdkConsistencyFinding
            {
                ComponentName = comp.Name,
                FindingType = "DimensionMismatch",
                Message = string.Format(
                    LocalizationService.Instance.Translate("PdkConsistency.HeightMismatch"),
                    comp.HeightMicrometers, template.HeightMicrometers, heightDiff),
                Severity = PdkFindingSeverity.Error,
                DeviationMicrometers = heightDiff
            });
    }

    private static void ComparePins(
        PdkComponentDraft comp, ComponentTemplate template, List<PdkConsistencyFinding> findings)
    {
        var templatePins = template.PinDefinitions
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var pin in comp.Pins)
        {
            if (!templatePins.TryGetValue(pin.Name, out var refPin))
            {
                findings.Add(new PdkConsistencyFinding
                {
                    ComponentName = comp.Name,
                    FindingType = "PinNotInTemplate",
                    Message = string.Format(
                        LocalizationService.Instance.Translate("PdkConsistency.PinNotInTemplate"),
                        pin.Name),
                    Severity = PdkFindingSeverity.Warning
                });
                continue;
            }

            var dx = Math.Abs(pin.OffsetXMicrometers - refPin.OffsetX);
            var dy = Math.Abs(pin.OffsetYMicrometers - refPin.OffsetY);
            var dist = Math.Sqrt(dx * dx + dy * dy);

            if (dist > PinPositionTolerance)
                findings.Add(new PdkConsistencyFinding
                {
                    ComponentName = comp.Name,
                    FindingType = "PinPositionMismatch",
                    Message = string.Format(
                        LocalizationService.Instance.Translate("PdkConsistency.PinPositionMismatch"),
                        pin.Name, pin.OffsetXMicrometers, pin.OffsetYMicrometers,
                        refPin.OffsetX, refPin.OffsetY, dist),
                    Severity = PdkFindingSeverity.Error,
                    DeviationMicrometers = dist
                });
        }
    }
}
