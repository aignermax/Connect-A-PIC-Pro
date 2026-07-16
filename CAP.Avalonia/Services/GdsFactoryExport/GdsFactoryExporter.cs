using System.Globalization;
using System.Text;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Components.PinKinds;
using CAP_Core.Export;
using CAP_Core.Routing;
using CAP_Core.Routing.MetalRouting;

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
    /// <param name="metalSpec">
    /// Process-derived metal routing parameters for electrical connections (issue #682):
    /// trace width, GDS layer/datatype, and waveguide-crossing policy. Electrical
    /// connections are emitted as metal on that layer instead of as optical waveguides.
    /// Null uses <see cref="MetalRoutingSpec.Default"/>.
    /// </param>
    public string Export(
        DesignCanvasViewModel canvas, GdsFactoryExportOptions options,
        MetalRoutingSpec? metalSpec = null)
    {
        var sb = new StringBuilder();
        var metal = metalSpec ?? MetalRoutingSpec.Default;
        AppendHeader(sb, canvas, options);
        GdsFactoryMetalTraceWriter.AppendHeaderConstants(sb, metal);
        AppendStubs(sb, canvas, options);
        var refIndex = 0;
        sb.AppendLine("c = gf.Component('ConnectAPIC_Design')");
        sb.AppendLine();
        sb.AppendLine("# Components");
        foreach (var comp in EnumerateExportableComponents(canvas))
            AppendPlacement(sb, comp, options, ref refIndex);
        sb.AppendLine();
        AppendConnections(sb, canvas, RoutingWaveguideKwarg(canvas), metal);
        AppendFooter(sb);
        return sb.ToString();
    }

    /// <summary>
    /// True when a connection between these two pins is a metal (electrical) trace: BOTH pins
    /// must be electrical (<see cref="PinKindHelper.IsElectrical(PhysicalPin?)"/>); a mixed
    /// optical+electrical or all-optical connection stays an optical waveguide (issue #686 review
    /// — the earlier "either pin" predicate would draw a mixed connection wholly on the metal
    /// layer, silently dropping the optical waveguide).
    /// </summary>
    private static bool IsMetalConnection(PhysicalPin? first, PhysicalPin? second) =>
        PinKindHelper.IsElectrical(first) && PinKindHelper.IsElectrical(second);

    /// <summary>
    /// The waveguide-sizing keyword argument for routed straights/bends. gdsfactory-native
    /// designs route with the PDK's cross-section (e.g. <c>cross_section='xs_nc'</c>): the
    /// generic <c>gf.components.straight(width=…)</c> resolves the 'strip' cross-section, which
    /// does not exist under a nitride PDK and crashed every export with a connection (#570 field
    /// test). Nazca/generic designs keep the explicit <c>width=WG_WIDTH</c>.
    /// </summary>
    private static string RoutingWaveguideKwarg(DesignCanvasViewModel canvas)
    {
        var crossSection = EnumerateExportableComponents(canvas)
            .Select(c => c.GdsFactoryRoutingCrossSection)
            .FirstOrDefault(x => !string.IsNullOrEmpty(x));
        return string.IsNullOrEmpty(crossSection) ? "width=WG_WIDTH" : $"cross_section='{crossSection}'";
    }

    /// <summary>
    /// Lists the distinct nazcaFunction names in the design that have no ubcpdk
    /// equivalent — shown in the export dialog as the stub-fallback list.
    /// </summary>
    public static IReadOnlyList<string> CollectUnmappedComponents(DesignCanvasViewModel canvas) =>
        EnumerateExportableComponents(canvas)
            // Components exported via a real gdsfactory factory are not stubs — don't report
            // them as "no gdsfactory equivalent". Use the SAME predicate as the placement path
            // so a bare (dotless) gdsFactoryFunction, which DOES fall through to a stub, is
            // still surfaced here (#570 review).
            .Where(comp => !UsesGdsFactoryFactory(comp))
            .Select(comp => comp.NazcaFunctionName)
            .Where(name => !string.IsNullOrEmpty(name) && UbcPdkCellMap.MapToUbcPdkCell(name) == null)
            .Distinct(StringComparer.Ordinal)
            .ToList()!;

    /// <summary>
    /// Detects an unsupported mixed-backend design: a gdsfactory-native PDK activates its own
    /// PDK in the export header (one process per chip), which makes ubcpdk cell lookups and a
    /// second gdsfactory module unresolvable. Returns the conflicting module names when more
    /// than one gdsfactory module is present, or a gdsfactory module coexists with
    /// ubcpdk-mapped components; empty when the design is single-backend (#570 review).
    /// </summary>
    public static IReadOnlyList<string> CollectBackendConflicts(
        DesignCanvasViewModel canvas, GdsFactoryExportOptions options)
    {
        var modules = GdsFactoryModules(canvas).ToList();
        var conflicts = new List<string>();
        if (modules.Count > 1)
            conflicts.AddRange(modules);
        else if (modules.Count == 1 &&
                 EnumerateExportableComponents(canvas).Any(c => UsesUbcPdkCell(c, options)))
            conflicts.Add(modules[0] + " + ubcpdk cells");
        return conflicts;
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

        var gdsfactoryModules = GdsFactoryModules(canvas).ToList();
        if (gdsfactoryModules.Count > 0)
        {
            // gdsfactory-backend design (e.g. CornerStone SiN via cspdk.sin300, #570): one
            // process per chip, so its own PDK is the active one — import and activate the
            // referenced module(s). We deliberately do NOT also activate ubcpdk/gpdk here: a
            // second activate would win and break these factories' layer lookups.
            foreach (var module in gdsfactoryModules)
            {
                sb.AppendLine($"import {module}");
                sb.AppendLine($"{module}.PDK.activate()");
            }
        }
        else if (options.Mode == GdsFactoryComponentMode.UbcPdkCells)
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
        sb.AppendLine();
        sb.AppendLine("WG_WIDTH = 0.45  # waveguide width in um");
        sb.AppendLine();
    }

    private static void AppendStubs(
        StringBuilder sb, DesignCanvasViewModel canvas, GdsFactoryExportOptions options)
    {
        var generated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var comp in EnumerateExportableComponents(canvas))
        {
            // A real gdsfactory factory / ubcpdk cell replaces the stub.
            if (UsesGdsFactoryFactory(comp)
                || UsesUbcPdkCell(comp, options))
                continue;
            GdsFactoryStubWriter.AppendStub(sb, comp, generated);
        }
    }

    private static bool UsesUbcPdkCell(Component comp, GdsFactoryExportOptions options) =>
        options.Mode == GdsFactoryComponentMode.UbcPdkCells
        && UbcPdkCellMap.MapToUbcPdkCell(comp.NazcaFunctionName) != null;

    /// <summary>
    /// True when the component exports via a real gdsfactory factory: it has a
    /// <see cref="Component.GdsFactoryFunction"/> that is module-qualified (contains a '.', e.g.
    /// "cspdk.sin300.mmi1x2"), so the header imports+activates its module and the placement can
    /// resolve the cell from the active PDK. A bare (dotless) name has no importable module and
    /// falls through to a stub rather than emitting an unresolvable call (#570).
    /// </summary>
    private static bool UsesGdsFactoryFactory(Component comp) =>
        GdsFactoryModuleOf(comp.GdsFactoryFunction) != null;

    /// <summary>
    /// The Python module part of a module-qualified gdsfactory function ("cspdk.sin300" from
    /// "cspdk.sin300.mmi1x2"), or null when the name is empty/bare. Single definition of the
    /// module-qualification rule, shared by the header import, the factory call, and the
    /// stub-vs-factory decision so they can never disagree (#570 review).
    /// </summary>
    private static string? GdsFactoryModuleOf(string? gdsFactoryFunction) =>
        !string.IsNullOrEmpty(gdsFactoryFunction) && gdsFactoryFunction!.Contains('.')
            ? gdsFactoryFunction.Substring(0, gdsFactoryFunction.LastIndexOf('.'))
            : null;

    /// <summary>The cell name of a module-qualified gdsfactory function ("mmi1x2" from "cspdk.sin300.mmi1x2").</summary>
    private static string GdsFactoryCellOf(string gdsFactoryFunction) =>
        gdsFactoryFunction.Substring(gdsFactoryFunction.LastIndexOf('.') + 1);

    /// <summary>
    /// Distinct Python modules of the design's gdsfactory-backend components. Each is imported
    /// and PDK-activated in the header (#570).
    /// </summary>
    private static IEnumerable<string> GdsFactoryModules(DesignCanvasViewModel canvas) =>
        EnumerateExportableComponents(canvas)
            .Select(c => GdsFactoryModuleOf(c.GdsFactoryFunction))
            .Where(m => m != null)
            .Select(m => m!)
            .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// Places one component: <c>rotate</c> about the cell origin, then <c>move</c> the
    /// origin to the mapper placement — equivalent to Nazca's <c>put('org', x, y, rot)</c>.
    /// </summary>
    private static void AppendPlacement(
        StringBuilder sb, Component comp, GdsFactoryExportOptions options,
        ref int refIndex)
    {
        var ci = CultureInfo.InvariantCulture;
        var placement = NazcaCoordinateMapper.GetCellPlacement(comp, rawOverrideAnchor: null);
        var x = placement.X.ToString("F2", ci);
        var y = placement.Y.ToString("F2", ci);
        var rot = placement.RotationDegrees.ToString("F0", ci);
        var varName = $"ref_{refIndex}";

        string factory;
        if (UsesGdsFactoryFactory(comp))
            // gdsfactory-backend component: resolve the cell from the PDK activated in the header.
            // cspdk exposes cells via the PDK registry (cspdk.sin300.cells / gf.get_component),
            // NOT as module attributes — "cspdk.sin300.mmi1x2()" raises AttributeError (#570 review).
            factory = $"gf.get_component('{GdsFactoryCellOf(comp.GdsFactoryFunction!)}')";
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

    private static void AppendConnections(
        StringBuilder sb, DesignCanvasViewModel canvas, string waveguideKwarg, MetalRoutingSpec metalSpec)
    {
        sb.AppendLine("# Waveguide connections");
        var metalStyle = metalSpec.ToTraceStyle();
        var opticalPaths = new List<IReadOnlyList<PathSegment>>();
        var electrical = new List<CAP_Core.Components.Connections.WaveguideConnection>();

        foreach (var connVm in canvas.Connections)
        {
            var conn = connVm.Connection;
            if (conn.StartPin?.ParentComponent?.IsAnalysisTool == true) continue;
            if (conn.EndPin?.ParentComponent?.IsAnalysisTool == true) continue;

            // Electrical connections are metal traces, not optical waveguides — draw them as a
            // polygon on the metal layer instead of a routed waveguide cell (issue #682). A
            // connection is metal only when BOTH pins are electrical; a mixed or all-optical
            // connection stays a waveguide (issue #686 review). Metal connections are
            // remembered so bridge markers can be placed where they cross optical paths.
            var metal = IsMetalConnection(conn.StartPin, conn.EndPin) ? metalStyle : null;
            if (metal != null)
                electrical.Add(conn);

            var segments = conn.GetPathSegments();
            if (segments.Count > 0)
            {
                GdsFactorySegmentWriter.AppendSegments(sb, segments, conn.StartPin, conn.EndPin, waveguideKwarg, metal);
                if (metal == null)
                    opticalPaths.Add(segments);
            }
            else if (conn.StartPin != null && conn.EndPin != null)
            {
                GdsFactorySegmentWriter.AppendPinToPinFallback(sb, conn.StartPin, conn.EndPin, waveguideKwarg, metal);
            }
        }

        foreach (var compVm in canvas.Components)
        {
            if (compVm.Component is ComponentGroup group)
                AppendGroupFrozenPaths(sb, group, waveguideKwarg, metalStyle, opticalPaths);
        }

        AppendBridgeMarkers(sb, electrical, opticalPaths, metalSpec);
        sb.AppendLine();
    }

    /// <summary>
    /// Emits bridge markers where electrical metal traces cross optical waveguide
    /// paths, when the active process requires bridges (#682). The trace geometry
    /// itself is emitted inline by the connection loop above.
    /// </summary>
    private static void AppendBridgeMarkers(
        StringBuilder sb,
        IReadOnlyList<CAP_Core.Components.Connections.WaveguideConnection> electrical,
        IReadOnlyList<IReadOnlyList<PathSegment>> opticalPaths,
        MetalRoutingSpec metalSpec)
    {
        if (metalSpec.CrossingPolicy != ElectricalCrossingPolicy.BridgeRequired || electrical.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine("# Electrical bridge markers (metal over waveguide)");
        foreach (var conn in electrical)
        {
            var segments = conn.GetPathSegments();
            if (segments.Count == 0)
                continue;

            var crossings = WaveguideCrossingDetector.FindCrossings(segments, opticalPaths);
            GdsFactoryMetalTraceWriter.AppendBridges(sb, crossings, metalSpec);
        }
    }

    /// <summary>
    /// Exports frozen waveguide paths from a ComponentGroup (and nested groups). A frozen path
    /// between two electrical pins is a metal trace, not a waveguide — mirrors the live
    /// connection loop above (issue #686 review). Frozen optical paths are collected as
    /// crossable geometry for bridge detection.
    /// </summary>
    private static void AppendGroupFrozenPaths(
        StringBuilder sb, ComponentGroup group, string waveguideKwarg,
        MetalTraceStyle metalStyle, List<IReadOnlyList<PathSegment>> opticalPaths)
    {
        foreach (var frozenPath in group.InternalPaths)
        {
            if (frozenPath?.Path?.Segments?.Count > 0)
            {
                var metal = IsMetalConnection(frozenPath.StartPin, frozenPath.EndPin) ? metalStyle : null;
                GdsFactorySegmentWriter.AppendSegments(
                    sb, frozenPath.Path.Segments, frozenPath.StartPin, frozenPath.EndPin, waveguideKwarg, metal);
                if (metal == null)
                    opticalPaths.Add(frozenPath.Path.Segments);
            }
        }

        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup nested)
                AppendGroupFrozenPaths(sb, nested, waveguideKwarg, metalStyle, opticalPaths);
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
