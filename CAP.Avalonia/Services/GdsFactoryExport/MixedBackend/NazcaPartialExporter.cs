using System.Globalization;
using System.Text;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_DataAccess.Persistence.PIR;

namespace CAP.Avalonia.Services.GdsFactoryExport.MixedBackend;

/// <summary>
/// Emits the Nazca half of a mixed-backend export (issue #646): a self-contained Nazca
/// script containing ONLY the instances whose raw-code override targets the Nazca backend,
/// each placed at its <see cref="NazcaCoordinateMapper"/> position. The resulting GDS is
/// imported by the gdsfactory host script via <c>gf.import_gds(...)</c> at the origin —
/// both emitters share the same absolute Y-up µm coordinate frame, so alignment carries
/// over without any per-instance transform. Everything else (PDK cells, stubs, waveguide
/// routes) is emitted by the gdsfactory host and must NOT appear here, or the merged GDS
/// would contain duplicate geometry.
/// </summary>
public class NazcaPartialExporter
{
    /// <summary>Cell name wrapping the Nazca-backend instances in the partial GDS.</summary>
    public const string PartCellName = "ConnectAPIC_NazcaPart";

    /// <summary>
    /// Identifiers of instances whose raw-code override targets the Nazca backend —
    /// exactly the instances this exporter renders (and the gdsfactory host skips).
    /// </summary>
    public static IReadOnlyList<string> CollectNazcaBackendOverrideIds(
        DesignCanvasViewModel canvas, IReadOnlyDictionary<string, NazcaCodeOverride>? overrides)
    {
        if (overrides == null) return Array.Empty<string>();
        return EnumerateNazcaOverrideComponents(canvas, overrides)
            .Select(c => c.Identifier)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Builds the Nazca script rendering only the Nazca-backend override instances.
    /// The GDS is written next to the script (same convention as the other exporters).
    /// </summary>
    /// <param name="canvas">The design canvas whose Nazca-backend instances to render.</param>
    /// <param name="overrides">Per-instance overrides keyed by component identifier.</param>
    public string Export(
        DesignCanvasViewModel canvas, IReadOnlyDictionary<string, NazcaCodeOverride> overrides)
    {
        var sb = new StringBuilder();
        sb.AppendLine("import nazca as nd");
        sb.AppendLine();

        var rawOverrides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var comp in EnumerateNazcaOverrideComponents(canvas, overrides))
            rawOverrides[comp.Identifier] = overrides[comp.Identifier].RawCode!;
        NazcaOverrideFactory.AppendFactories(sb, rawOverrides);

        AppendPlacements(sb, canvas, overrides);
        AppendFooter(sb);
        return sb.ToString();
    }

    private static void AppendPlacements(
        StringBuilder sb, DesignCanvasViewModel canvas,
        IReadOnlyDictionary<string, NazcaCodeOverride> overrides)
    {
        sb.AppendLine($"with nd.Cell(name='{PartCellName}') as part:");
        var placed = false;
        foreach (var comp in EnumerateNazcaOverrideComponents(canvas, overrides))
        {
            AppendSinglePlacement(sb, comp, overrides[comp.Identifier]);
            placed = true;
        }
        if (!placed)
            sb.AppendLine("    pass");
        sb.AppendLine();
        sb.AppendLine("part.put()");
    }

    /// <summary>
    /// Places one override instance using the same contract as the full Nazca export:
    /// org-anchored on the persisted cell-internal bbox corner when available, so the
    /// rendered geometry lands on the component's grid rectangle (issue #561).
    /// </summary>
    private static void AppendSinglePlacement(StringBuilder sb, Component comp, NazcaCodeOverride ovr)
    {
        var ci = CultureInfo.InvariantCulture;
        (double XMin, double YMax)? anchor = null;
        if (ovr.OverrideBboxXMinMicrometers is { } xMin && ovr.OverrideBboxYMaxMicrometers is { } yMax)
            anchor = (xMin, yMax);

        var placement = NazcaCoordinateMapper.GetCellPlacement(comp, anchor);
        var x = placement.X.ToString("F2", ci);
        var y = placement.Y.ToString("F2", ci);
        var rot = placement.RotationDegrees.ToString("F0", ci);
        var factory = NazcaOverrideFactory.FactoryName(comp.Identifier);

        sb.AppendLine(anchor != null
            ? $"    {factory}().put('org', {x}, {y}, {rot})  # {comp.Identifier} (bbox-anchored)"
            : $"    {factory}().put({x}, {y}, {rot})  # {comp.Identifier}");
    }

    private static void AppendFooter(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("# Export GDS with filename matching this script");
        sb.AppendLine("import os");
        sb.AppendLine("gds_filename = os.path.splitext(os.path.abspath(__file__))[0] + '.gds'");
        sb.AppendLine("nd.export_gds(filename=gds_filename)");
        sb.AppendLine("print(f'GDS exported to: {gds_filename}')");
    }

    private static IEnumerable<Component> EnumerateNazcaOverrideComponents(
        DesignCanvasViewModel canvas, IReadOnlyDictionary<string, NazcaCodeOverride> overrides)
    {
        foreach (var compVm in canvas.Components)
        {
            var comp = compVm.Component;
            if (comp.IsAnalysisTool) continue;
            if (comp is ComponentGroup group)
            {
                foreach (var child in group.GetAllComponentsRecursive())
                    if (!child.IsAnalysisTool && IsNazcaBackendOverride(child, overrides))
                        yield return child;
            }
            else if (IsNazcaBackendOverride(comp, overrides))
            {
                yield return comp;
            }
        }
    }

    private static bool IsNazcaBackendOverride(
        Component comp, IReadOnlyDictionary<string, NazcaCodeOverride> overrides) =>
        comp.Identifier != null
        && overrides.TryGetValue(comp.Identifier, out var o)
        && !string.IsNullOrWhiteSpace(o.RawCode)
        && o.Backend == OverrideBackend.Nazca;
}
