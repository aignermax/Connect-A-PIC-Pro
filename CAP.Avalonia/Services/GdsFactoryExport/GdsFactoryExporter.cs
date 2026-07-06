using System.Globalization;
using System.Text;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_DataAccess.Persistence.PIR;

namespace CAP.Avalonia.Services.GdsFactoryExport;

/// <summary>
/// Second layout emitter next to the Nazca exporter (issue #581, option 1): generates a
/// self-contained gdsfactory Python script from the design. Placement, pins, and routed
/// segments follow the same coordinate contract as the Nazca export (both targets are
/// Y-up), delegated to <see cref="NazcaCoordinateMapper"/>. The script writes its GDS
/// next to itself (same convention as Nazca), so the existing script-runner finds it.
/// </summary>
public class GdsFactoryExporter
{
    /// <summary>Exports the design to a gdsfactory Python script.</summary>
    /// <param name="canvas">The design canvas to export.</param>
    /// <param name="options">Component representation mode (stubs vs. ubcpdk cells).</param>
    /// <param name="overrides">Per-instance overrides; gdsfactory-backend ones are emitted as
    /// component factories. Null skips override handling.</param>
    public string Export(
        DesignCanvasViewModel canvas, GdsFactoryExportOptions options,
        IReadOnlyDictionary<string, NazcaCodeOverride>? overrides = null)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, canvas, options);
        AppendOverrideFactories(sb, canvas, overrides);
        AppendStubs(sb, canvas, options, overrides);
        var refIndex = 0;
        sb.AppendLine("c = gf.Component('ConnectAPIC_Design')");
        sb.AppendLine();
        sb.AppendLine("# Components");
        foreach (var comp in EnumerateExportableComponents(canvas))
            AppendPlacement(sb, comp, options, overrides, ref refIndex);
        sb.AppendLine();
        AppendConnections(sb, canvas);
        AppendFooter(sb);
        return sb.ToString();
    }

    /// <summary>
    /// Lists the distinct nazcaFunction names in the design that have no ubcpdk
    /// equivalent — shown in the export dialog as the stub-fallback list.
    /// </summary>
    public static IReadOnlyList<string> CollectUnmappedComponents(DesignCanvasViewModel canvas) =>
        EnumerateExportableComponents(canvas)
            .Select(comp => comp.NazcaFunctionName)
            .Where(name => !string.IsNullOrEmpty(name) && UbcPdkCellMap.MapToUbcPdkCell(name) == null)
            .Distinct(StringComparer.Ordinal)
            .ToList()!;

    /// <summary>
    /// Identifiers of instances whose override is written for Nazca — the gdsfactory export
    /// cannot run that code, so those instances use their ubcpdk/stub geometry instead of the
    /// custom override. Surfaced as a pre-export warning so the divergence is visible.
    /// </summary>
    public static IReadOnlyList<string> CollectBackendMismatches(
        DesignCanvasViewModel canvas, IReadOnlyDictionary<string, NazcaCodeOverride>? overrides)
    {
        if (overrides == null) return Array.Empty<string>();
        return EnumerateExportableComponents(canvas)
            .Where(c => overrides.TryGetValue(c.Identifier, out var o)
                        && !string.IsNullOrWhiteSpace(o.RawCode)
                        && o.Backend == OverrideBackend.Nazca)
            .Select(c => c.Identifier)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Returns the gdsfactory-backend override RawCode for a component, or null.</summary>
    private static string? GdsFactoryOverrideCode(
        Component comp, IReadOnlyDictionary<string, NazcaCodeOverride>? overrides)
    {
        if (overrides != null
            && overrides.TryGetValue(comp.Identifier, out var o)
            && !string.IsNullOrWhiteSpace(o.RawCode)
            && o.Backend == OverrideBackend.GdsFactory)
            return o.RawCode;
        return null;
    }

    private static string OverrideFactoryName(Component comp) =>
        "override_" + System.Text.RegularExpressions.Regex.Replace(comp.Identifier, @"[^a-zA-Z0-9_]", "_");

    /// <summary>Emits one factory per gdsfactory-backend override: the user's code wrapped in a
    /// function that returns the `component` it defines.</summary>
    private static void AppendOverrideFactories(
        StringBuilder sb, DesignCanvasViewModel canvas,
        IReadOnlyDictionary<string, NazcaCodeOverride>? overrides)
    {
        var generated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var comp in EnumerateExportableComponents(canvas))
        {
            var code = GdsFactoryOverrideCode(comp, overrides);
            if (code == null) continue;
            var name = OverrideFactoryName(comp);
            if (!generated.Add(name)) continue;

            sb.AppendLine($"def {name}() -> gf.Component:");
            sb.AppendLine("    import gdsfactory as gf");
            foreach (var line in code.Replace("\r\n", "\n").Split('\n'))
                sb.AppendLine("    " + line);
            sb.AppendLine("    return component");
            sb.AppendLine();
        }
    }

    private static IEnumerable<Component> EnumerateExportableComponents(DesignCanvasViewModel canvas)
    {
        foreach (var compVm in canvas.Components)
        {
            var comp = compVm.Component;
            if (comp.IsAnalysisTool) continue;
            if (comp is ComponentGroup group)
            {
                foreach (var child in group.GetAllComponentsRecursive())
                    if (!child.IsAnalysisTool)
                        yield return child;
            }
            else
            {
                yield return comp;
            }
        }
    }

    private static void AppendHeader(StringBuilder sb, DesignCanvasViewModel canvas, GdsFactoryExportOptions options)
    {
        sb.AppendLine("import os");
        sb.AppendLine("import gdsfactory as gf");
        if (options.Mode == GdsFactoryComponentMode.UbcPdkCells)
        {
            sb.AppendLine("from ubcpdk import PDK");
            sb.AppendLine("PDK.activate()");
        }
        else
        {
            // gdsfactory 9.x refuses layer lookups without an active PDK — the generic
            // PDK is enough for the self-contained stub geometry.
            sb.AppendLine("gf.gpdk.PDK.activate()");
        }

        // gdsfactory-backend PDKs (e.g. CornerStone SiN via cspdk.sin300, #570): import each
        // referenced module and activate its PDK so its factories resolve. A single-process
        // design references one such module; activating it last makes it the active PDK.
        foreach (var module in GdsFactoryModules(canvas))
        {
            sb.AppendLine($"import {module}");
            sb.AppendLine($"{module}.PDK.activate()");
        }
        sb.AppendLine();
        sb.AppendLine("WG_WIDTH = 0.45  # waveguide width in um");
        sb.AppendLine();
    }

    private static void AppendStubs(
        StringBuilder sb, DesignCanvasViewModel canvas, GdsFactoryExportOptions options,
        IReadOnlyDictionary<string, NazcaCodeOverride>? overrides)
    {
        var generated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var comp in EnumerateExportableComponents(canvas))
        {
            // A gdsfactory override / real gdsfactory factory / ubcpdk cell all replace the stub.
            if (GdsFactoryOverrideCode(comp, overrides) != null
                || !string.IsNullOrEmpty(comp.GdsFactoryFunction)
                || UsesUbcPdkCell(comp, options))
                continue;
            GdsFactoryStubWriter.AppendStub(sb, comp, generated);
        }
    }

    private static bool UsesUbcPdkCell(Component comp, GdsFactoryExportOptions options) =>
        options.Mode == GdsFactoryComponentMode.UbcPdkCells
        && UbcPdkCellMap.MapToUbcPdkCell(comp.NazcaFunctionName) != null;

    /// <summary>
    /// Distinct Python modules of the design's gdsfactory-backend components — the package
    /// part of each <see cref="Component.GdsFactoryFunction"/> (e.g. "cspdk.sin300" from
    /// "cspdk.sin300.mmi1x2"). Each is imported and PDK-activated in the header (#570).
    /// </summary>
    private static IEnumerable<string> GdsFactoryModules(DesignCanvasViewModel canvas) =>
        EnumerateExportableComponents(canvas)
            .Select(c => c.GdsFactoryFunction)
            .Where(f => !string.IsNullOrEmpty(f) && f!.Contains('.'))
            .Select(f => f!.Substring(0, f!.LastIndexOf('.')))
            .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// Places one component: <c>rotate</c> about the cell origin, then <c>move</c> the
    /// origin to the mapper placement — equivalent to Nazca's <c>put('org', x, y, rot)</c>.
    /// </summary>
    private static void AppendPlacement(
        StringBuilder sb, Component comp, GdsFactoryExportOptions options,
        IReadOnlyDictionary<string, NazcaCodeOverride>? overrides, ref int refIndex)
    {
        var ci = CultureInfo.InvariantCulture;
        var placement = NazcaCoordinateMapper.GetCellPlacement(comp, rawOverrideAnchor: null);
        var x = placement.X.ToString("F2", ci);
        var y = placement.Y.ToString("F2", ci);
        var rot = placement.RotationDegrees.ToString("F0", ci);
        var varName = $"ref_{refIndex}";

        string factory;
        if (GdsFactoryOverrideCode(comp, overrides) != null)
            factory = $"{OverrideFactoryName(comp)}()";
        else if (!string.IsNullOrEmpty(comp.GdsFactoryFunction))
            // gdsfactory-backend component: call its real factory (its PDK is activated in the header).
            factory = $"{comp.GdsFactoryFunction}()";
        else if (UsesUbcPdkCell(comp, options))
            factory = $"gf.get_component('{UbcPdkCellMap.MapToUbcPdkCell(comp.NazcaFunctionName)}')";
        else
            factory = $"{GdsFactoryStubWriter.StubFunctionName(comp)}({StubArguments(comp)})";

        sb.AppendLine($"{varName} = c.add_ref({factory})  # {comp.Identifier}");
        sb.AppendLine($"{varName}.rotate({rot})");
        sb.AppendLine($"{varName}.move(({x}, {y}))");

        refIndex++;
    }

    /// <summary>Forwards stored parameters for parametric straights (length=…).</summary>
    private static string StubArguments(Component comp) =>
        NazcaCoordinateMapper.IsParametricStraight(comp.NazcaFunctionName, comp.NazcaFunctionParameters)
        && !string.IsNullOrEmpty(comp.NazcaFunctionParameters)
            ? comp.NazcaFunctionParameters
            : string.Empty;

    private static void AppendConnections(StringBuilder sb, DesignCanvasViewModel canvas)
    {
        sb.AppendLine("# Waveguide connections");
        foreach (var connVm in canvas.Connections)
        {
            var conn = connVm.Connection;
            if (conn.StartPin?.ParentComponent?.IsAnalysisTool == true) continue;
            if (conn.EndPin?.ParentComponent?.IsAnalysisTool == true) continue;

            var segments = conn.GetPathSegments();
            if (segments.Count > 0)
                GdsFactorySegmentWriter.AppendSegments(sb, segments, conn.StartPin, conn.EndPin);
            else if (conn.StartPin != null && conn.EndPin != null)
                GdsFactorySegmentWriter.AppendPinToPinFallback(sb, conn.StartPin, conn.EndPin);
        }

        foreach (var compVm in canvas.Components)
        {
            if (compVm.Component is ComponentGroup group)
                AppendGroupFrozenPaths(sb, group);
        }
        sb.AppendLine();
    }

    private static void AppendGroupFrozenPaths(StringBuilder sb, ComponentGroup group)
    {
        foreach (var frozenPath in group.InternalPaths)
        {
            if (frozenPath?.Path?.Segments?.Count > 0)
                GdsFactorySegmentWriter.AppendSegments(
                    sb, frozenPath.Path.Segments, frozenPath.StartPin, frozenPath.EndPin);
        }

        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup nested)
                AppendGroupFrozenPaths(sb, nested);
        }
    }

    private static void AppendFooter(StringBuilder sb)
    {
        sb.AppendLine("# Export GDS with filename matching this script");
        sb.AppendLine("gds_path = os.path.splitext(os.path.abspath(__file__))[0] + '.gds'");
        sb.AppendLine("c.write_gds(gds_path)");
        sb.AppendLine("print(f'GDS exported to: {gds_path}')");
    }
}
