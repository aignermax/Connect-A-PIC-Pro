using System.Globalization;
using System.Text;
using CAP.Avalonia.Services.MetalRouting;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Components.PinKinds;
using CAP_Core.Export;
using CAP_Core.Export.InterconnectRouting;
using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting;
using CAP_Core.Routing.MetalRouting;

namespace CAP.Avalonia.Services;

/// <summary>
/// Simple Nazca exporter for the physical coordinate system.
/// Exports components and waveguide connections to Python/Nazca code.
/// </summary>
public class SimpleNazcaExporter
{
    /// <summary>
    /// GDS (layer, datatype) pair for pin/port labels (TEXT elements), emitted as a
    /// Python layer tuple: the gdsfactory port-label convention (1, 10), which is also
    /// the default our GDS re-import pin detector reads
    /// (<c>GdsPinDetectionOptions.PortLayers</c>, issue #808). Labels are emitted as
    /// <c>nd.Annotation</c> — <c>nd.text</c> renders stroked POLYGONS, not GDS TEXT
    /// records, so a label-based pin detector would never see it.
    /// Internal so <see cref="NazcaRawCodeCellWriter"/> labels raw-code pins identically.
    /// </summary>
    internal const string PortLabelLayer = "(1, 10)";

    /// <summary>
    /// Optional source of global interconnect settings (waveguide width/bend radius/GDS layer,
    /// issue #574). When null, the historical export defaults (<see cref="InterconnectSettings"/>)
    /// are used.
    /// </summary>
    public Func<InterconnectSettings>? SettingsSource { get; set; }

    /// <summary>
    /// Exports the full design to a Python/Nazca script.
    /// Component stub cells carry a TEXT label per optical pin and the design's external
    /// ports (fiber couplers) carry top-cell labels — both on the gdsfactory port-label
    /// layer (1, 10) so the GDS re-import detects named pins (issue #808).
    /// </summary>
    /// <param name="canvas">The design canvas to export.</param>
    /// <param name="pdkModuleName">Optional PDK module name (e.g., "siepic_ebeam_pdk") for import.</param>
    /// <param name="emitVerification">
    /// When true, appends a machine-readable verification epilog (issue #565) that dumps
    /// every placed instance's ACTUAL world pin positions — reported by the same nazca
    /// engine that writes the GDS — to '&lt;script&gt;.pins.json' next to the script.
    /// </param>
    /// <param name="metalSpec">
    /// Process-derived metal routing parameters for electrical connections (issue #682):
    /// trace width, GDS layer/datatype, and waveguide-crossing policy. Electrical
    /// connections are emitted as metal on that layer instead of as optical waveguides.
    /// Null uses <see cref="MetalRoutingSpec.Default"/>.
    /// </param>
    /// <param name="skippedConnections">
    /// Optional collector: appended with an "Start.Pin → End.Pin" description for every
    /// connection or frozen group path left out of the geometry because its route is a
    /// placeholder or invalid (<see cref="ExportableConnections.IsExportable(RoutedPath?)"/>).
    /// Populated as a side effect of THIS write, so the caller's post-export report always
    /// matches what actually landed in the script — a separately recomputed snapshot could
    /// diverge if routing is still running in the background.
    /// </param>
    /// <param name="unresolvedCrossings">
    /// Optional collector: appended with an "Start.Pin → End.Pin" description for every
    /// EXPORTED optical connection <c>WaveguideConnectionManager</c>'s sibling-crossing pass
    /// flagged (<see cref="RoutedPath.IsBlockedFallback"/>) and that a bridge marker does not
    /// resolve — the geometry is rendered (a real, non-placeholder crossing is not a reason to
    /// omit it), but the layout still deserves a second look.
    /// </param>
    /// <param name="library">
    /// Optional component library for raw-code inlining (<see cref="NazcaRawCodeCellWriter"/>):
    /// placed components whose template carries nazca-backend raw code (GDS imports, custom
    /// Python cells) then export their REAL geometry instead of a box stub / demofab
    /// heuristic. Null keeps the legacy behavior.
    /// </param>
    /// <param name="exportWarnings">
    /// Optional collector: appended with one description per UNIQUE raw-code template
    /// whose geometry source is missing (a deleted .gds file) and that therefore exports
    /// as a placeholder box stub — a template placed ten times warns once, matching the
    /// once-per-template fallback emission.
    /// </param>
    public string Export(
        DesignCanvasViewModel canvas,
        string? pdkModuleName = null,
        bool emitVerification = false,
        MetalRoutingSpec? metalSpec = null,
        List<string>? skippedConnections = null,
        List<string>? unresolvedCrossings = null,
        IEnumerable<ComponentTemplate>? library = null,
        List<string>? exportWarnings = null)
    {
        var sb = new StringBuilder();
        var metal = metalSpec ?? MetalRoutingSpec.Default;
        var interconnectSettings = SettingsSource?.Invoke() ?? new InterconnectSettings();
        var rawCodePlan = NazcaRawCodeCellWriter.BuildPlan(canvas, include: null, library, exportWarnings);
        var wrapperPlan = NazcaPinLabelWrapperWriter.BuildPlan(canvas, include: null, rawCodePlan);

        AppendHeader(sb, interconnectSettings, metal);
        AppendPdkComponentStubs(sb, canvas, include: null, rawCodePlan, wrapperPlan);
        NazcaRawCodeCellWriter.AppendCells(sb, rawCodePlan, CultureInfo.InvariantCulture);
        NazcaPinLabelWrapperWriter.AppendCells(sb, wrapperPlan);
        var componentNames = AppendComponents(sb, canvas, emitVerification, rawCodePlan: rawCodePlan, wrapperPlan: wrapperPlan);
        AppendConnections(
            sb, canvas, componentNames, metal, interconnectSettings.GdsLayer,
            skippedConnections, unresolvedCrossings);
        AppendFooter(sb);
        SiepicCellUpgradeWriter.AppendUpgradeBlock(sb, canvas);
        if (emitVerification)
            AppendVerificationEpilog(sb);

        return sb.ToString();
    }

    /// <summary>
    /// Exports only the components matching <paramref name="include"/> — the nazca-native
    /// group of a mixed-backend export. Connections are NOT emitted: routed
    /// waveguides are owned by the main gdsfactory script, which imports the partial GDS
    /// this script renders and merges it into the final output. The top cell is named
    /// <paramref name="topCellName"/> so it cannot collide with the gdsfactory design cell.
    /// </summary>
    /// <param name="canvas">The design canvas to export.</param>
    /// <param name="include">Predicate selecting the components to render.</param>
    /// <param name="topCellName">Name of the partial design's top cell.</param>
    /// <param name="metalSpec">Metal routing parameters; null uses <see cref="MetalRoutingSpec.Default"/>.</param>
    /// <param name="library">
    /// Optional component library for raw-code inlining — see <see cref="Export"/>.
    /// </param>
    /// <param name="exportWarnings">
    /// Optional collector for missing-source raw-code fallbacks — see <see cref="Export"/>.
    /// </param>
    public string ExportPartial(
        DesignCanvasViewModel canvas,
        Func<Component, bool> include,
        string topCellName,
        MetalRoutingSpec? metalSpec = null,
        IEnumerable<ComponentTemplate>? library = null,
        List<string>? exportWarnings = null)
    {
        var sb = new StringBuilder();
        var metal = metalSpec ?? MetalRoutingSpec.Default;
        var interconnectSettings = SettingsSource?.Invoke() ?? new InterconnectSettings();
        var rawCodePlan = NazcaRawCodeCellWriter.BuildPlan(canvas, include, library, exportWarnings);
        var wrapperPlan = NazcaPinLabelWrapperWriter.BuildPlan(canvas, include, rawCodePlan);

        AppendHeader(sb, interconnectSettings, metal);
        AppendPdkComponentStubs(sb, canvas, include, rawCodePlan, wrapperPlan);
        NazcaRawCodeCellWriter.AppendCells(sb, rawCodePlan, CultureInfo.InvariantCulture);
        NazcaPinLabelWrapperWriter.AppendCells(sb, wrapperPlan);
        AppendComponents(sb, canvas, emitVerification: false, include, topCellName, rawCodePlan, wrapperPlan);
        AppendFooter(sb);
        SiepicCellUpgradeWriter.AppendUpgradeBlock(sb, canvas, include);

        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, InterconnectSettings settings, MetalRoutingSpec metal)
    {
        var ci = CultureInfo.InvariantCulture;
        sb.AppendLine("import nazca as nd");
        sb.AppendLine("import nazca.demofab as demo");
        sb.AppendLine("from nazca.interconnects import Interconnect");
        sb.AppendLine();
        sb.AppendLine("# PDK Configuration");
        sb.AppendLine($"WG_WIDTH = {settings.WidthMicrometers.ToString("0.0###", ci)}  # Waveguide width in µm");
        sb.AppendLine($"BEND_RADIUS = {settings.BendRadiusMicrometers.ToString("0.###", ci)}  # Minimum bend radius in µm");
        if (settings.GdsLayer.HasValue)
            sb.AppendLine($"WG_LAYER = {settings.GdsLayer.Value}  # Waveguide GDS layer");
        sb.AppendLine();
        NazcaMetalTraceWriter.AppendHeaderConstants(sb, metal);
        sb.AppendLine("# Create interconnect for waveguide routing");
        sb.AppendLine(settings.GdsLayer.HasValue
            ? "ic = Interconnect(width=WG_WIDTH, radius=BEND_RADIUS, layer=WG_LAYER)"
            : "ic = Interconnect(width=WG_WIDTH, radius=BEND_RADIUS)");
        sb.AppendLine();
    }

    /// <summary>
    /// Generates standalone Nazca cell definitions for PDK components.
    /// Each unique PDK function AND parameter set used in the design gets a stub cell
    /// with correct dimensions and pin positions — no external PDK install needed
    /// (parameterized names carry a hash, <see cref="NazcaStubNaming"/>, issue #783).
    /// ComponentGroups are flattened — stubs are generated for all child components.
    /// SiEPIC stubs are placeholders only: after <c>nd.export_gds()</c> the script's
    /// klayout post-pass (<see cref="SiepicCellUpgradeWriter"/>) swaps their content
    /// for the real foundry geometry when the PDK is installed.
    /// </summary>
    private static void AppendPdkComponentStubs(
        StringBuilder sb, DesignCanvasViewModel canvas, Func<Component, bool>? include = null,
        RawCodeExportPlan? rawCodePlan = null, PinLabelWrapperPlan? wrapperPlan = null)
    {
        var ci = CultureInfo.InvariantCulture;
        var generated = new HashSet<string>(StringComparer.Ordinal);
        var plan = rawCodePlan ?? RawCodeExportPlan.Empty;

        foreach (var compVm in canvas.Components)
        {
            var comp = compVm.Component;
            if (comp.IsAnalysisTool) continue;
            if (comp is ComponentGroup group)
            {
                foreach (var child in group.GetAllComponentsRecursive())
                {
                    if (child.IsAnalysisTool) continue;
                    if (include != null && !include(child)) continue;
                    AppendComponentStub(sb, child, generated, ci, plan, wrapperPlan);
                }
            }
            else
            {
                if (include != null && !include(comp)) continue;
                AppendComponentStub(sb, comp, generated, ci, plan, wrapperPlan);
            }
        }
    }

    /// <summary>
    /// Generates a PDK stub for a single component if required.
    /// Dedupes by STUB name, not function name: parameterized components carry a
    /// parameters hash in the name (issue #783), so each distinct parameter set
    /// generates its own stub while identical placements still share one.
    /// A component in the raw-code plan renders its real geometry via
    /// <see cref="NazcaRawCodeCellWriter"/> instead — no stub — unless it is a
    /// missing-source fallback, which keeps a box stub under the wrapper's
    /// function name so the placement call is identical either way.
    /// </summary>
    private static void AppendComponentStub(
        StringBuilder sb, Component comp, HashSet<string> generated, CultureInfo ci,
        RawCodeExportPlan plan, PinLabelWrapperPlan? wrapperPlan = null)
    {
        if (plan.TryGetEntry(comp, out var rawEntry))
        {
            if (!rawEntry.IsFallback || !generated.Add(rawEntry.FunctionName))
                return;
            AppendStandardComponentStub(
                sb, rawEntry.Template.Name, rawEntry.FunctionName, comp, ci,
                NazcaCoordinateMapper.GetStubAnchor(comp));
            return;
        }

        // Pin-label-wrapped components place the real module cell inside the
        // wrapper — the (dead, never-called) box stub would be pure noise.
        if (wrapperPlan is not null && wrapperPlan.TryGetEntry(comp, out _))
            return;

        var funcName = comp.NazcaFunctionName;
        if (string.IsNullOrEmpty(funcName) || !RequiresStub(funcName))
            return;
        var stubName = NazcaStubNaming.StubName(funcName, comp.NazcaFunctionParameters);
        if (!generated.Add(stubName))
            return;

        if (NazcaCoordinateMapper.IsParametricStraight(funcName, comp.NazcaFunctionParameters))
            AppendParametricStraightStub(sb, funcName, comp, ci);
        else
            AppendStandardComponentStub(sb, funcName, stubName, comp, ci);
    }

    /// <summary>
    /// Checks if a function requires a stub definition.
    /// Returns true for real PDK functions and demo_pdk functions.
    /// </summary>
    private static bool RequiresStub(string funcName) =>
        NazcaCoordinateMapper.IsPdkFunction(funcName) ||
        funcName.StartsWith("demo_pdk.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Generates a parametric straight waveguide stub that uses nd.strt() with length parameter.
    /// The cell-internal layout follows the <see cref="NazcaCoordinateMapper"/> contract:
    /// the cell is org-anchored on the offset (ox, oy) the mapper places it by — for a
    /// parametric straight that is the FIRST pin's offset, NOT NazcaOriginOffsetY. The
    /// straight's centre line coincides with its pins (same OffsetY on a straight), so it
    /// sits at oy - firstPin.OffsetY, and every pin renders at the plain Y negation of its
    /// app offset relative to org (oy - OffsetY). Using the mapper's own anchor keeps the
    /// rendered pins coincident with <see cref="NazcaCoordinateMapper.GetPinNazcaPosition"/>;
    /// the old NazcaOriginOffsetY-based anchor differed from the placement and shifted the
    /// rendered geometry off the pins (issue #565).
    /// </summary>
    private static void AppendParametricStraightStub(
        StringBuilder sb, string funcName, Component comp, CultureInfo ci)
    {
        // The cell is rotation-independent (placement applies .put(rot)); use the
        // UNROTATED first-pin offset as the org anchor (oy), mirroring the mapper. The
        // straight's centre line coincides with its pins, so it sits at oy - firstPin.oy.
        var (anchorX, anchorY) = NazcaCoordinateMapper.GetStubAnchor(comp);
        var firstPin = comp.PhysicalPins.FirstOrDefault();
        var firstPinY = firstPin != null
            ? NazcaCoordinateMapper.GetUnrotatedPinOffset(comp, firstPin).OffsetY
            : 0;
        var strtY = NazcaCoordinateMapper.NormalizeZero(anchorY - firstPinY).ToString("F2", ci);

        // Sanitize function name for valid Python identifier (replace non-alphanumeric/underscore chars)
        var pythonFuncName = System.Text.RegularExpressions.Regex.Replace(funcName, @"[^a-zA-Z0-9_]", "_");

        sb.AppendLine($"def {pythonFuncName}(length=100, **kwargs):");
        sb.AppendLine($"    \"\"\"Auto-generated parametric straight waveguide stub for {funcName}.\"\"\"");
        // The cell name is an f-string so each length gets its OWN cell
        // ('demo.shallow.strt_100') — a plain string would bake the literal
        // "{length}" into the name, collapsing every length into one cell.
        sb.AppendLine($"    with nd.Cell(name=f'{funcName}_{{length}}') as cell:");
        sb.AppendLine($"        # Use nd.strt() for proper waveguide with specified length");
        sb.AppendLine($"        nd.strt(length=length, width=0.45, layer=1).put(0, {strtY})");

        // The black-box body frame on demofab's bb_body layer (1003, 0) — the
        // same documentation layer demofab's own cells draw their frames on:
        // the bare straight is 0.45 µm tall while the app component is W×H, so
        // without the frame the cell bbox (and with it the re-imported
        // placement position) would sit ~(H−0.45)/2 off the original.
        var w = comp.WidthMicrometers;
        var h = comp.HeightMicrometers;
        var bx0 = NazcaCoordinateMapper.NormalizeZero(-anchorX).ToString("F2", ci);
        var by0 = NazcaCoordinateMapper.NormalizeZero(anchorY - h).ToString("F2", ci);
        var bx1 = NazcaCoordinateMapper.NormalizeZero(w - anchorX).ToString("F2", ci);
        var by1 = NazcaCoordinateMapper.NormalizeZero(anchorY).ToString("F2", ci);
        sb.AppendLine(
            $"        nd.Polygon(points=[({bx0},{by0}),({bx1},{by0}),({bx1},{by1}),({bx0},{by1})], " +
            "layer=(1003, 0)).put(0, 0)  # bb_body frame (documentation layer)");

        // Generate pins from the UNROTATED offsets, relative to org (the mapper anchor);
        // a straight's pins share the centre line, so their local Y is oy - OffsetY = 0.
        foreach (var pin in comp.PhysicalPins)
        {
            var (uox, uoy) = NazcaCoordinateMapper.GetUnrotatedPinOffset(comp, pin);
            var py = NazcaCoordinateMapper.NormalizeZero(anchorY - uoy).ToString("F2", ci);
            var pa = NazcaCoordinateMapper.NormalizeZero(-pin.AngleDegrees).ToString("F0", ci);

            // For straight waveguides: input pin at x=0, output pin at x=length.
            // The label anchors exactly on the pin so re-import detects it there (#808).
            // nd.Pin is an optical Nazca port; electrical pins are not optical ports and
            // must not be emitted as waveguide stubs (#519) — but they still get the
            // port label, or a re-import could never see the pin.
            if (uox == 0)
            {
                if (pin.MatterType == MatterType.Light)
                    sb.AppendLine($"        nd.Pin('{pin.Name}').put(0, {py}, {pa})");
                sb.AppendLine($"        nd.Annotation(text='{EscapePythonString(pin.Name)}', layer={PortLabelLayer}).put(0, {py})");
            }
            else
            {
                if (pin.MatterType == MatterType.Light)
                    sb.AppendLine($"        nd.Pin('{pin.Name}').put(length, {py}, {pa})");
                sb.AppendLine($"        nd.Annotation(text='{EscapePythonString(pin.Name)}', layer={PortLabelLayer}).put(length, {py})");
            }
        }

        sb.AppendLine($"    return cell");
        sb.AppendLine();
    }

    /// <summary>
    /// Generates a standard non-parametric component stub using a polygon box.
    /// The cell-internal layout follows the placement contract of
    /// <see cref="NazcaCoordinateMapper"/>: the geometry bbox is
    /// [-ox, oy-H] .. [W-ox, oy] around the cell origin, because at rotation 0
    /// the org pin is put at (PhysicalX+ox, -(PhysicalY+oy)) and the box top edge
    /// must lie oy above org. Pins render exactly where the app model places them
    /// (plain Y negation), so exported waveguides meet the stub pins.
    /// The stub box doubles as the anchor the klayout post-pass
    /// (<see cref="SiepicCellUpgradeWriter"/>) fills with real foundry geometry
    /// for SiEPIC cells — same cell name, so instances keep their placement.
    /// Every optical pin additionally gets a TEXT label on <see cref="PortLabelLayer"/>
    /// anchored on the pin, so a re-import of the cell detects named pins there
    /// (issue #808).
    /// </summary>
    /// <param name="stubName">
    /// Cell/function name to emit: <paramref name="funcName"/> plus the parameters
    /// hash for parameterized components (<see cref="NazcaStubNaming"/>, issue #783),
    /// so two parameter sets never share one cell.
    /// </param>
    /// <param name="anchorOverride">
    /// Optional cell anchor replacing the calibrated origin offset: the raw-code
    /// fallback stub anchors on <see cref="NazcaCoordinateMapper.GetStubAnchor"/>
    /// (the same value the placement derives its bbox from) because raw-code
    /// components carry no calibrated offset — the plain (0, 0) default would
    /// render the box and its pins one cell height below the placed position.
    /// </param>
    private static void AppendStandardComponentStub(
        StringBuilder sb, string funcName, string stubName, Component comp, CultureInfo ci,
        (double X, double Y)? anchorOverride = null)
    {
        var w = comp.WidthMicrometers;
        var h = comp.HeightMicrometers;

        // Sanitize stub name for valid Python identifier (replace non-alphanumeric/underscore chars)
        var pythonFuncName = System.Text.RegularExpressions.Regex.Replace(stubName, @"[^a-zA-Z0-9_]", "_");

        // Define cell once, return cached instance on each call
        sb.AppendLine($"with nd.Cell(name='{stubName}') as _{pythonFuncName}_cell:");
        // funcName is PDK-controlled (a raw-code template's Name in fallback mode) —
        // sanitize so a hostile name cannot break out of the docstring.
        sb.AppendLine($"    \"\"\"Auto-generated stub for {SanitizePythonComment(funcName)} ({comp.WidthMicrometers.ToString(ci)}x{comp.HeightMicrometers.ToString(ci)} µm).\"\"\"");

        // Stubs are only generated for PDK-named components (see RequiresStub), whose
        // placement always uses the calibrated origin offset — (0,0) means org at the
        // box top-left. Example: GC with offset (0, 9.5), H=19 → polygon (0,-9.5)..(W,9.5).
        double offsetX = anchorOverride?.X ?? comp.NazcaOriginOffsetX;
        double offsetY = anchorOverride?.Y ?? comp.NazcaOriginOffsetY;

        var px0 = NazcaCoordinateMapper.NormalizeZero(-offsetX).ToString("F2", ci);
        var py0 = NazcaCoordinateMapper.NormalizeZero(offsetY - h).ToString("F2", ci);
        var px1 = NazcaCoordinateMapper.NormalizeZero(w - offsetX).ToString("F2", ci);
        var py1 = NazcaCoordinateMapper.NormalizeZero(offsetY).ToString("F2", ci);

        // Purely electrical components (probe/bond pads, #682) are metal structures —
        // draw their body on the metal layer instead of the waveguide layer.
        var isMetalComponent = comp.PhysicalPins.Count > 0
            && comp.PhysicalPins.All(p => p.MatterType == MatterType.Electricity);
        var bodyLayer = isMetalComponent ? "METAL_LAYER" : "1";

        sb.AppendLine($"    nd.Polygon(points=[({px0},{py0}),({px1},{py0}),({px1},{py1}),({px0},{py1})], layer={bodyLayer}).put(0, 0)");

        // Pins relative to org: local = (UnrotatedOffsetX-ox, oy-UnrotatedOffsetY), the
        // plain Y negation of the app pin offsets (NazcaCoordinateMapper.GetPinNazcaPosition
        // contract). The UNROTATED offsets matter: the cell is rotation-independent
        // (placement applies .put(rot)) and one stub is shared by instances at different
        // rotations — live offsets would bake the first instance's rotation into the
        // shared cell (NazcaCoordinateMapper.GetUnrotatedPinOffset).
        foreach (var pin in comp.PhysicalPins)
        {
            var (pinOffsetX, pinOffsetY) = NazcaCoordinateMapper.GetUnrotatedPinOffset(comp, pin);
            var px = NazcaCoordinateMapper.NormalizeZero(pinOffsetX - offsetX).ToString("F2", ci);
            var py = NazcaCoordinateMapper.NormalizeZero(offsetY - pinOffsetY).ToString("F2", ci);
            var pa = NazcaCoordinateMapper.NormalizeZero(-pin.AngleDegrees).ToString("F0", ci);
            // nd.Pin is an optical Nazca port: electrical pins get no Pin (#519) —
            // but they DO get the port label, or a re-import could never see them
            // (a purely electrical component like a bond pad carries no other pin trace).
            if (pin.MatterType == MatterType.Light)
                sb.AppendLine($"    nd.Pin('{pin.Name}').put({px}, {py}, {pa})");
            // Pin label at the same anchor — re-import detects the named pin there (#808).
            sb.AppendLine($"    nd.Annotation(text='{EscapePythonString(pin.Name)}', layer={PortLabelLayer}).put({px}, {py})");
        }

        sb.AppendLine();
        sb.AppendLine($"def {pythonFuncName}(**kwargs):");
        sb.AppendLine($"    return _{pythonFuncName}_cell");
        sb.AppendLine();
    }

    private static Dictionary<Component, string> AppendComponents(
        StringBuilder sb, DesignCanvasViewModel canvas, bool emitVerification = false,
        Func<Component, bool>? include = null, string topCellName = "ConnectAPIC_Design",
        RawCodeExportPlan? rawCodePlan = null, PinLabelWrapperPlan? wrapperPlan = null)
    {
        sb.AppendLine("def create_design():");
        sb.AppendLine($"    with nd.Cell(name='{topCellName}') as design:");
        sb.AppendLine();
        sb.AppendLine("        # Components");
        var componentNames = new Dictionary<Component, string>();
        int compIndex = 0;
        var ci = CultureInfo.InvariantCulture;
        var plan = rawCodePlan ?? RawCodeExportPlan.Empty;

        foreach (var compVm in canvas.Components)
        {
            var comp = compVm.Component;
            if (comp.IsAnalysisTool) continue;
            // gdsfactory-native components (e.g. CornerStone SiN) have no Nazca representation —
            // skip them rather than emit a meaningless demofab stub. They export via the
            // gdsfactory export instead. Only in the full export, though: when an include
            // predicate partitions the design (mixed-backend partial), it alone decides —
            // skipping here would silently drop raw-code nazca components that also carry a
            // gdsfactory function name.
            if (include == null && !string.IsNullOrEmpty(comp.GdsFactoryFunction)) continue;
            if (comp is ComponentGroup group)
            {
                // Flatten group: export all child components at their absolute positions
                foreach (var child in group.GetAllComponentsRecursive())
                {
                    if (child.IsAnalysisTool) continue;
                    if (include == null && !string.IsNullOrEmpty(child.GdsFactoryFunction)) continue;
                    if (include != null && !include(child)) continue;
                    AppendSingleComponent(sb, child, componentNames, ref compIndex, ci, plan, wrapperPlan);
                }
            }
            else
            {
                if (include != null && !include(comp)) continue;
                AppendSingleComponent(sb, comp, componentNames, ref compIndex, ci, plan, wrapperPlan);
            }
        }

        if (emitVerification)
            AppendVerificationRegistry(sb, componentNames);

        sb.AppendLine();
        return componentNames;
    }

    /// <summary>
    /// Exposes the placed instances to the verification epilog. The comp_N variables
    /// are locals of create_design(), but the epilog runs at module level after the
    /// GDS export — a module-level registry bridges the two scopes.
    /// </summary>
    private static void AppendVerificationRegistry(
        StringBuilder sb, Dictionary<Component, string> componentNames)
    {
        var pairs = string.Join(", ", componentNames.Values.Select(n => $"('{n}', {n})"));
        sb.AppendLine();
        sb.AppendLine("        # Instance registry for the alignment verification epilog.");
        sb.AppendLine("        global _verify_instances");
        sb.AppendLine($"        _verify_instances = [{pairs}]");
    }

    /// <summary>
    /// Emits the self-verification footer (issue #565): the TRUE world pin positions of
    /// every placed instance, asked from the same nazca engine that wrote the GDS — the
    /// GDS itself carries no pins. The result is written as JSON next to the script so
    /// tests (and tooling) can compare it against <see cref="NazcaCoordinateMapper"/>.
    /// </summary>
    private static void AppendVerificationEpilog(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("# --- Alignment verification (machine-readable) ---");
        sb.AppendLine("import json as _json");
        sb.AppendLine("_verify = {}");
        sb.AppendLine("for _name, _inst in _verify_instances:");
        sb.AppendLine("    _pins = {}");
        sb.AppendLine("    for _pn, _pin in _inst.pin.items():");
        sb.AppendLine("        _px, _py, _pa = _pin.xya()");
        sb.AppendLine("        # float() unwraps numpy scalars, which json cannot serialize.");
        sb.AppendLine("        _pins[_pn] = [float(_px), float(_py), float(_pa)]");
        sb.AppendLine("    _verify[_name] = _pins");
        sb.AppendLine("with open(os.path.splitext(script_path)[0] + '.pins.json', 'w') as _f:");
        sb.AppendLine("    _json.dump(_verify, _f)");
    }

    /// <summary>
    /// Appends a single component placement to the Nazca script and records its variable name.
    /// </summary>
    private static void AppendSingleComponent(
        StringBuilder sb, Component comp, Dictionary<Component, string> componentNames,
        ref int compIndex, CultureInfo ci, RawCodeExportPlan? rawCodePlan = null,
        PinLabelWrapperPlan? wrapperPlan = null)
    {
        var varName = $"comp_{compIndex}";

        var placement = NazcaCoordinateMapper.GetCellPlacement(comp, rawOverrideAnchor: null);
        var nazcaX = placement.X.ToString("F2", ci);
        var nazcaY = placement.Y.ToString("F2", ci);
        var rot = placement.RotationDegrees.ToString("F0", ci);
        // A raw-code component (GDS import / custom Python cell) calls its inlined
        // wrapper — same call shape for the real-geometry wrapper and the
        // missing-source fallback box stub (<see cref="NazcaRawCodeCellWriter"/>).
        // A pin-label-wrapped component (electrical pins on a real module call)
        // calls its wrapper cell (<see cref="NazcaPinLabelWrapperWriter"/>).
        var nazcaFunc = rawCodePlan is not null && rawCodePlan.TryGetEntry(comp, out var rawEntry)
            ? $"{rawEntry.FunctionName}()"
            : wrapperPlan is not null && wrapperPlan.TryGetEntry(comp, out var wrapperEntry)
                ? $"{wrapperEntry.FunctionName}()"
                : GetNazcaFunction(comp);

        // Diagnostic logging (Issue #334): trace coordinate transform for each component.
        // originOffset is the effective put-position offset relative to the editor
        // top-left, derived from the mapper placement so the diagnosis can never
        // drift from the emitted coordinates.
        double originOffsetX = NazcaCoordinateMapper.NormalizeZero(placement.X - comp.PhysicalX);
        double originOffsetY = NazcaCoordinateMapper.NormalizeZero(-placement.Y - comp.PhysicalY);
        sb.AppendLine($"        # COORD: {comp.Identifier} " +
                      $"editor=({comp.PhysicalX.ToString("F2", ci)},{comp.PhysicalY.ToString("F2", ci)}) " +
                      $"originOffset=({originOffsetX.ToString("F2", ci)},{originOffsetY.ToString("F2", ci)}) " +
                      $"nazca=({nazcaX},{nazcaY}) rot={rot}");

        // Pin coordinate diagnostics: show expected Nazca pin positions for alignment verification.
        foreach (var pin in comp.PhysicalPins)
        {
            var (pinNazcaX, pinNazcaY) = NazcaCoordinateMapper.GetPinNazcaPosition(pin);
            sb.AppendLine($"        # PIN: {pin.Name} expected_nazca=({pinNazcaX.ToString("F2", ci)},{pinNazcaY.ToString("F2", ci)})");
        }

        // Nazca's Cell.put() defaults to anchoring on the cell's first pin
        // (typically 'a0'), NOT on the cell origin. For demofab components
        // whose 'a0' isn't at (0,0) — e.g. demo.mmi2x2_dp has a0 at y=+4,
        // demo.dbr has a0 at y=-70 — the default anchor shifts the placed
        // cell relative to where Lunima's NazcaOriginOffset math expects.
        // Result: visible Y mismatch in the rendered GDS even though the
        // calibration editor (which reads the same Python-rendered cell)
        // shows alignment as correct.
        //
        // Pin 'org' is the cell-origin marker every demofab/SiEPIC cell
        // ships (set up via bbu.put_boundingbox('org', ...)). Anchoring
        // on 'org' explicitly makes .put() place the cell origin at the
        // computed (x, y) — which IS the contract Lunima's calibration
        // and export math both assume.
        sb.AppendLine($"        {varName} = {nazcaFunc}.put('org', {nazcaX}, {nazcaY}, {rot})  # {comp.Identifier}");

        // External ports of the design get a top-cell label on the port-label layer,
        // so re-imports and label-based tools find the circuit's interface (#808).
        AppendExternalPortLabels(sb, comp, ci);

        // Record the variable only after its put-line was emitted: a half-failed append
        // must not leave a name pointing at a component that was never placed.
        componentNames[comp] = varName;
        compIndex++;
    }

    /// <summary>
    /// Emits one top-cell port label (GDS TEXT on <see cref="PortLabelLayer"/>) per optical
    /// pin of a fiber-interface coupler — the design's external ports. What counts as a
    /// coupler follows <see cref="LightSourceClassifier"/> (grating/edge couplers), the same
    /// single source of truth the simulation uses to bind external inputs/outputs: both
    /// laser-enabled input couplers and listen-only output couplers are labeled. The label
    /// sits at the pin's world position (the same plain-Y-negation transform as every other
    /// pin coordinate in this script) and is named "{Identifier}_{PinName}" so multiple
    /// couplers never produce colliding port names. Non-coupler components emit nothing:
    /// their pins are already labeled inside their stub cells.
    /// </summary>
    private static void AppendExternalPortLabels(StringBuilder sb, Component comp, CultureInfo ci)
    {
        if (!LightSourceClassifier.IsLightInjectingCoupler(comp))
            return;

        foreach (var pin in comp.PhysicalPins)
        {
            // Optical pins only — an electrical pin (e.g. a detector's contacts) is not
            // an optical port and must not become one on re-import (#519).
            if (pin.MatterType != MatterType.Light) continue;

            var (x, y) = NazcaCoordinateMapper.GetPinNazcaPosition(pin);
            var px = x.ToString("F2", ci);
            var py = y.ToString("F2", ci);
            var portName = EscapePythonString($"{comp.Identifier}_{pin.Name}");
            sb.AppendLine($"        nd.Annotation(text='{portName}', layer={PortLabelLayer}).put({px}, {py})");
        }
    }

    /// <summary>
    /// Escapes a name for emission inside a single-quoted Python string literal:
    /// backslashes and single quotes would otherwise break (or inject into) the
    /// generated script, and raw line breaks would split the statement (#808).
    /// Internal so <see cref="NazcaRawCodeCellWriter"/> escapes pin labels identically.
    /// </summary>
    internal static string EscapePythonString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("'", "\\'", StringComparison.Ordinal)
             .Replace("\r", "\\r", StringComparison.Ordinal)
             .Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>
    /// Makes an arbitrary PDK-controlled string (template name, PDK source) safe for
    /// emission inside a single-line Python COMMENT or a <c>"""</c> docstring of the
    /// generated script: CR/LF would break out of the line and inject raw script lines,
    /// a literal <c>"""</c> would terminate the docstring early. The characters are
    /// stripped outright (not escaped) — the text is informational only, so losing a
    /// quote sequence beats complicating the generated script. Internal so
    /// <see cref="NazcaRawCodeCellWriter"/> sanitizes its wrapper comments identically.
    /// </summary>
    internal static string SanitizePythonComment(string value) =>
        value.Replace("\"\"\"", string.Empty, StringComparison.Ordinal)
             .Replace("\r", string.Empty, StringComparison.Ordinal)
             .Replace("\n", string.Empty, StringComparison.Ordinal);

    private static void AppendConnections(
        StringBuilder sb,
        DesignCanvasViewModel canvas,
        Dictionary<Component, string> componentNames,
        MetalRoutingSpec metalSpec,
        int? gdsLayer = null,
        List<string>? skippedConnections = null,
        List<string>? unresolvedCrossings = null)
    {
        var hasFrozenPaths = canvas.Components.Any(vm => vm.Component is ComponentGroup);
        if (canvas.Connections.Count == 0 && !hasFrozenPaths)
            return;

        sb.AppendLine("        # Waveguide Connections");

        var metalStyle = metalSpec.ToTraceStyle();
        var metalConnections = new List<WaveguideConnection>();
        var unresolvedCrossingCandidates = new List<WaveguideConnection>();
        foreach (var connVm in canvas.Connections)
        {
            var conn = connVm.Connection;
            // Skip connections that touch a virtual analysis tool — those pins
            // have no physical fab counterpart.
            if (conn.StartPin?.ParentComponent?.IsAnalysisTool == true) continue;
            if (conn.EndPin?.ParentComponent?.IsAnalysisTool == true) continue;

            // A placeholder (self-crossing fallback with no optical model) or invalid
            // (bend radius violation) route must never render as geometry — the design
            // still exports, just without this connection's geometry. A missing route is
            // NOT skipped: it falls back to the pin-to-pin straight below, same as before.
            if (ExportableConnections.TryRecordSkip(conn.RoutedPath, conn.StartPin, conn.EndPin, skippedConnections))
                continue;

            // Electrical connections are metal traces, not optical waveguides — emit them on
            // the process metal layer/width instead of the waveguide layer (issue #682). A
            // connection is metal only when BOTH pins are electrical; a mixed optical+electrical
            // or all-optical connection stays a waveguide (issue #686 review — the earlier
            // "either pin" predicate would draw a mixed connection wholly on the metal layer,
            // silently dropping the optical waveguide). Metal connections are remembered so
            // bridge markers can be placed where they cross optical paths (below).
            var metal = IsMetalConnection(conn.StartPin, conn.EndPin) ? metalStyle : null;
            if (metal != null)
                metalConnections.Add(conn);
            else if (conn.IsBlockedFallback)
                // Real (non-placeholder) geometry that WaveguideConnectionManager's sibling-
                // crossing pass still flagged — it renders (below), but the layout deserves a
                // second look unless a bridge marker actually resolves the crossing.
                unresolvedCrossingCandidates.Add(conn);

            // Explicit routing style (issue #574) applies to OPTICAL waveguides only:
            // point-to-point styles export a single Nazca primitive (strt/sinebend/cobra)
            // on the waveguide layer instead of the routed segments; Bend and Euler return
            // null here and fall through to AppendSegmentExport below, which writes their
            // exact canvas stub–arc–stub segments (a lone nd.bend/nd.euler cannot land on
            // an arbitrary end pin). An electrical connection must stay a metal trace
            // (issue #682) — never emit it as an optical primitive even if a style was
            // set, so styled export is gated on metal == null.
            if (metal == null)
            {
                var styledLine = NazcaConnectionStyleWriter.Format(conn, gdsLayer);
                if (styledLine != null)
                {
                    sb.AppendLine(styledLine);
                    continue;
                }
            }

            // Routed connections export their real segments; only routeless
            // connections fall back to a p2p interconnect.
            var segments = conn.GetPathSegments();

            if (segments.Count > 0)
                AppendSegmentExport(sb, segments, conn.StartPin, conn.EndPin, metal);
            else
                AppendFallbackExport(sb, conn.StartPin, conn.EndPin, componentNames, metal);
        }

        // Export frozen waveguide paths from ComponentGroups
        foreach (var compVm in canvas.Components)
        {
            if (compVm.Component is ComponentGroup group)
                AppendGroupFrozenPaths(sb, group, metalStyle, componentNames, skippedConnections);
        }

        AppendBridgeMarkers(sb, canvas, metalConnections, metalSpec);
        CollectUnresolvedCrossings(unresolvedCrossingCandidates, metalConnections, metalSpec, unresolvedCrossings);

        sb.AppendLine();
    }

    /// <summary>
    /// Reports the flagged connections a bridge marker does NOT resolve: a crossing is
    /// bridge-resolved only when it is a metal↔optical pair under
    /// <see cref="ElectricalCrossingPolicy.BridgeRequired"/> — exactly the condition
    /// <see cref="AppendBridgeMarkers"/> uses to decide whether to draw a marker at all, so a
    /// candidate that crosses no exported metal trace (an optical×optical crossing, or any
    /// crossing under a policy that never draws a marker) is genuinely unresolved.
    /// </summary>
    private static void CollectUnresolvedCrossings(
        IReadOnlyList<WaveguideConnection> candidates,
        IReadOnlyList<WaveguideConnection> metalConnections,
        MetalRoutingSpec metalSpec,
        List<string>? unresolvedCrossings)
    {
        if (unresolvedCrossings == null || candidates.Count == 0)
            return;

        var bridgesCrossings = metalSpec.CrossingPolicy == ElectricalCrossingPolicy.BridgeRequired;
        foreach (var candidate in candidates)
        {
            bool resolvedByBridge = bridgesCrossings && candidate.RoutedPath != null
                && metalConnections.Any(metalConn =>
                    metalConn.RoutedPath != null
                    && PathIntersectionDetector.Crosses(candidate.RoutedPath, metalConn.RoutedPath));
            if (!resolvedByBridge)
                unresolvedCrossings.Add(ExportableConnections.Describe(candidate.StartPin, candidate.EndPin));
        }
    }

    /// <summary>
    /// Emits bridge markers for electrical metal traces (issue #682): when the active
    /// process requires bridges, a marker is placed wherever a metal trace crosses an
    /// optical waveguide path. The trace geometry itself is emitted inline by the
    /// connection loop above (on the process metal layer).
    /// </summary>
    private static void AppendBridgeMarkers(
        StringBuilder sb,
        DesignCanvasViewModel canvas,
        IReadOnlyList<WaveguideConnection> metalConnections,
        MetalRoutingSpec metalSpec)
    {
        if (metalSpec.CrossingPolicy != ElectricalCrossingPolicy.BridgeRequired
            || metalConnections.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine("        # Electrical bridge markers (metal over waveguide)");

        var opticalPaths = CollectOpticalPaths(canvas);
        foreach (var conn in metalConnections)
        {
            var segments = conn.GetPathSegments();
            if (segments.Count == 0)
                continue;

            var crossings = WaveguideCrossingDetector.FindCrossings(segments, opticalPaths);
            NazcaMetalTraceWriter.AppendBridges(sb, crossings, metalSpec);
        }
    }

    /// <summary>
    /// Collects the routed segment lists of all optical connections and frozen group
    /// paths — the geometry a metal trace can cross and that bridges must span.
    /// </summary>
    private static List<IReadOnlyList<PathSegment>> CollectOpticalPaths(DesignCanvasViewModel canvas)
    {
        var paths = new List<IReadOnlyList<PathSegment>>();
        foreach (var connVm in canvas.Connections)
        {
            var conn = connVm.Connection;
            // Both-pins-electrical connections render as metal; everything else
            // (including mixed pairs, which stay waveguides) is crossable geometry.
            if (IsMetalConnection(conn.StartPin, conn.EndPin)) continue;
            if (conn.StartPin?.ParentComponent?.IsAnalysisTool == true) continue;
            if (conn.EndPin?.ParentComponent?.IsAnalysisTool == true) continue;
            // A placeholder/invalid route never reaches export — it must not count as
            // crossable geometry either, or a metal trace would get a bridge marker over
            // a waveguide that isn't actually drawn.
            if (!conn.IsExportable()) continue;
            var segments = conn.GetPathSegments();
            if (segments.Count > 0)
                paths.Add(segments);
        }

        foreach (var compVm in canvas.Components)
        {
            if (compVm.Component is ComponentGroup group)
                CollectGroupFrozenPaths(group, paths);
        }
        return paths;
    }

    /// <summary>Adds all frozen waveguide paths of a group (and nested groups) to the list.</summary>
    private static void CollectGroupFrozenPaths(ComponentGroup group, List<IReadOnlyList<PathSegment>> paths)
    {
        foreach (var frozenPath in group.InternalPaths)
        {
            if (frozenPath?.Path?.Segments?.Count > 0 && frozenPath.Path.IsExportable())
                paths.Add(frozenPath.Path.Segments);
        }
        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup nested)
                CollectGroupFrozenPaths(nested, paths);
        }
    }

    /// <summary>
    /// Exports all frozen waveguide paths from a ComponentGroup (and nested groups) as Nazca
    /// segments. A frozen path between two electrical pins is a metal trace, not an optical
    /// waveguide — the same classification the live connection loop above applies (issue #686
    /// review: this frozen-group path used to call <see cref="AppendSegmentExport"/> without the
    /// metal style at all, so a frozen electrical route always rendered as a waveguide). A
    /// frozen path with placeholder or invalid geometry is left out just like a live
    /// connection — freezing (grouping) a connection must not bypass the export filter. A
    /// frozen path with NO route at all (a connection frozen before it was ever routed keeps
    /// an empty <c>RoutedPath</c>, not null) renders the same pin-to-pin fallback a routeless
    /// live connection gets, instead of silently vanishing.
    /// </summary>
    private static void AppendGroupFrozenPaths(
        StringBuilder sb, ComponentGroup group, MetalTraceStyle metalStyle,
        Dictionary<Component, string> componentNames, List<string>? skippedConnections = null)
    {
        foreach (var frozenPath in group.InternalPaths)
        {
            if (frozenPath == null) continue;

            var metal = IsMetalConnection(frozenPath.StartPin, frozenPath.EndPin) ? metalStyle : null;
            var segments = frozenPath.Path?.Segments;
            if (segments == null || segments.Count == 0)
            {
                AppendFallbackExport(sb, frozenPath.StartPin, frozenPath.EndPin, componentNames, metal);
                continue;
            }

            if (ExportableConnections.TryRecordSkip(
                    frozenPath.Path, frozenPath.StartPin, frozenPath.EndPin, skippedConnections))
                continue;
            AppendSegmentExport(sb, segments, frozenPath.StartPin, frozenPath.EndPin, metal);
        }

        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup nestedGroup)
                AppendGroupFrozenPaths(sb, nestedGroup, metalStyle, componentNames, skippedConnections);
        }
    }

    /// <summary>
    /// Appends segment-by-segment Nazca export for a routed connection.
    /// Uses absolute .put(x, y, angle) for EVERY segment to avoid coordinate accumulation
    /// errors that occur with Nazca's chaining syntax (.put() without coordinates).
    /// App→Nazca conversion of path geometry is the plain Y negation
    /// (<see cref="NazcaCoordinateMapper.ToNazca"/>); pins live at the same conversion,
    /// so no start-pin offset correction exists — cells are placed so their rendered
    /// pins coincide with the app pins.
    /// </summary>
    /// <param name="sb">Target script builder.</param>
    /// <param name="segments">Routed path segments in editor (app) coordinates.</param>
    /// <param name="startPin">Start pin, used for single-straight pin-to-pin geometry.</param>
    /// <param name="endPin">End pin, used for single-straight pin-to-pin geometry.</param>
    internal static void AppendSegmentExport(
        StringBuilder sb, IReadOnlyList<PathSegment> segments,
        PhysicalPin? startPin = null, PhysicalPin? endPin = null,
        MetalTraceStyle? metal = null)
    {
        // Single straight segment: compute geometry directly from both pin positions
        // so the waveguide hits both pins exactly even if the stored segment drifts.
        if (segments.Count == 1 && segments[0] is StraightSegment && startPin != null && endPin != null)
        {
            sb.AppendLine(FormatStraightSegmentFromPins(startPin, endPin, metal));
            return;
        }

        foreach (var segment in segments)
        {
            var (nStartX, nStartY) = NazcaCoordinateMapper.ToNazca(segment.StartPoint.X, segment.StartPoint.Y);
            var (nEndX, nEndY) = NazcaCoordinateMapper.ToNazca(segment.EndPoint.X, segment.EndPoint.Y);

            sb.AppendLine(FormatSegmentAbsolute(segment, nStartX, nStartY, nEndX, nEndY, metal));
        }
    }

    /// <summary>
    /// The trailing <c>width=…, layer=(…, …)</c> kwargs that place a segment on the metal
    /// routing layer; empty for optical segments (which use the Nazca default layer).
    /// </summary>
    private static string MetalKwargs(MetalTraceStyle? metal) =>
        metal is null ? string.Empty : $", width={metal.WidthLiteral}, layer={metal.LayerTuple}";

    /// <summary>
    /// True when a connection between these two pins is a metal (electrical) trace: BOTH pins
    /// must be electrical (<see cref="PinKindHelper.IsElectrical(PhysicalPin?)"/>); a mixed
    /// optical+electrical or all-optical connection stays an optical waveguide (issue #686 review).
    /// </summary>
    private static bool IsMetalConnection(PhysicalPin? first, PhysicalPin? second) =>
        PinKindHelper.IsElectrical(first) && PinKindHelper.IsElectrical(second);

    /// <summary>
    /// Formats a path segment (straight or bend) with absolute Nazca positions.
    /// Straight segments compute length and angle from the transformed Nazca endpoints,
    /// ensuring the exported geometry matches the actual endpoint positions.
    /// Bend segments use stored radius/sweep with negated angles for Y-flip.
    /// </summary>
    private static string FormatSegmentAbsolute(
        PathSegment segment, double nazcaStartX, double nazcaStartY,
        double nazcaEndX, double nazcaEndY, MetalTraceStyle? metal = null)
    {
        var ci = CultureInfo.InvariantCulture;
        return segment switch
        {
            StraightSegment => FormatStraightAbsolute(
                nazcaStartX, nazcaStartY, nazcaEndX, nazcaEndY, ci, metal),
            BendSegment bend => FormatBendAbsolute(bend, nazcaStartX, nazcaStartY, ci, metal),
            _ => $"        # Unknown segment type: {segment.GetType().Name}"
        };
    }

    /// <summary>
    /// Formats a straight segment by computing length and angle from Nazca start/end positions.
    /// This is more robust than using stored editor-space angles, because the Nazca Y-flip
    /// is applied to the actual endpoints rather than relying on angle negation.
    /// </summary>
    private static string FormatStraightAbsolute(
        double nazcaStartX, double nazcaStartY,
        double nazcaEndX, double nazcaEndY, CultureInfo ci, MetalTraceStyle? metal = null)
    {
        double dx = nazcaEndX - nazcaStartX;
        double dy = nazcaEndY - nazcaStartY;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double angleDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;

        var l = length.ToString("F2", ci);
        var x = NazcaCoordinateMapper.NormalizeZero(nazcaStartX).ToString("F2", ci);
        var y = NazcaCoordinateMapper.NormalizeZero(nazcaStartY).ToString("F2", ci);
        var a = NazcaCoordinateMapper.NormalizeZero(angleDeg).ToString("F2", ci);
        return $"        nd.strt(length={l}{MetalKwargs(metal)}).put({x}, {y}, {a})";
    }

    /// <summary>
    /// Formats a bend segment with absolute Nazca start position.
    /// The radius is invariant under Y-flip; the sweep angle and start angle are negated.
    /// </summary>
    private static string FormatBendAbsolute(
        BendSegment bend, double nazcaX, double nazcaY, CultureInfo ci, MetalTraceStyle? metal = null)
    {
        var radius = bend.RadiusMicrometers.ToString("F2", ci);
        var sweepAngle = NazcaCoordinateMapper.NormalizeZero(-bend.SweepAngleDegrees).ToString("F2", ci);
        var x = NazcaCoordinateMapper.NormalizeZero(nazcaX).ToString("F2", ci);
        var y = NazcaCoordinateMapper.NormalizeZero(nazcaY).ToString("F2", ci);
        var angle = NazcaCoordinateMapper.NormalizeZero(-bend.StartAngleDegrees).ToString("F2", ci);
        return $"        nd.bend(radius={radius}, angle={sweepAngle}{MetalKwargs(metal)}).put({x}, {y}, {angle})";
    }

    /// <summary>
    /// Formats a straight waveguide segment using absolute Nazca pin positions.
    /// Computes length and angle from start pin to end pin in Nazca coordinates,
    /// ensuring the waveguide reaches both pins exactly.
    /// </summary>
    private static string FormatStraightSegmentFromPins(
        PhysicalPin startPin, PhysicalPin endPin, MetalTraceStyle? metal = null)
    {
        var ci = CultureInfo.InvariantCulture;
        var (sx, sy) = NazcaCoordinateMapper.GetPinNazcaPosition(startPin);
        var (ex, ey) = NazcaCoordinateMapper.GetPinNazcaPosition(endPin);

        double dx = ex - sx;
        double dy = ey - sy;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double angleDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;

        var x = NazcaCoordinateMapper.NormalizeZero(sx).ToString("F2", ci);
        var y = NazcaCoordinateMapper.NormalizeZero(sy).ToString("F2", ci);
        var a = NazcaCoordinateMapper.NormalizeZero(angleDeg).ToString("F2", ci);
        var l = length.ToString("F2", ci);

        return $"        nd.strt(length={l}{MetalKwargs(metal)}).put({x}, {y}, {a})";
    }

    /// <summary>
    /// Formats a single path segment as a Nazca Python call.
    /// </summary>
    /// <param name="segment">The path segment to format.</param>
    /// <param name="isFirst">If true, includes absolute coordinates; if false, chains with .put().</param>
    /// <param name="startPin">Optional start pin for correct Nazca coordinate calculation (Issue #329 fix)</param>
    internal static string FormatSegment(PathSegment segment, bool isFirst = true, PhysicalPin? startPin = null)
    {
        var ci = CultureInfo.InvariantCulture;

        return segment switch
        {
            StraightSegment straight => FormatStraightSegment(straight, ci, isFirst, startPin),
            BendSegment bend => FormatBendSegment(bend, ci, isFirst, startPin),
            _ => $"        # Unknown segment type: {segment.GetType().Name}"
        };
    }

    private static string FormatStraightSegment(
        StraightSegment straight, CultureInfo ci, bool isFirst, PhysicalPin? startPin = null)
    {
        // For chained segments, use the forward-projected length instead of Euclidean
        // distance. Nazca's nd.strt() goes forward along the propagation direction,
        // so if the segment is slightly diagonal, the Euclidean length would overshoot.
        var length = isFirst
            ? straight.LengthMicrometers
            : ProjectForwardLength(straight);
        var lengthStr = length.ToString("F2", ci);

        if (isFirst)
        {
            double nazcaX;
            double nazcaY;
            if (startPin != null)
            {
                // Anchor the chain on the pin's world position so the waveguide
                // starts exactly where the component's stub pin sits.
                (nazcaX, nazcaY) = NazcaCoordinateMapper.GetPinNazcaPosition(startPin);
            }
            else
            {
                // Without pin info the segment's own start point is the best anchor.
                (nazcaX, nazcaY) = NazcaCoordinateMapper.ToNazca(
                    straight.StartPoint.X, straight.StartPoint.Y);
            }

            var x = NazcaCoordinateMapper.NormalizeZero(nazcaX).ToString("F2", ci);
            var y = NazcaCoordinateMapper.NormalizeZero(nazcaY).ToString("F2", ci);
            var angle = NazcaCoordinateMapper.NormalizeZero(-straight.StartAngleDegrees).ToString("F2", ci);
            return $"        nd.strt(length={lengthStr}).put({x}, {y}, {angle})";
        }

        return $"        nd.strt(length={lengthStr}).put()";
    }

    /// <summary>
    /// Projects a straight segment's length onto its propagation direction.
    /// Nazca's nd.strt(length=L) goes forward by L along the current angle,
    /// so if the segment is slightly diagonal, we need the forward component only.
    /// </summary>
    private static double ProjectForwardLength(StraightSegment straight)
    {
        double dx = straight.EndPoint.X - straight.StartPoint.X;
        double dy = straight.EndPoint.Y - straight.StartPoint.Y;
        double angleRad = straight.StartAngleDegrees * Math.PI / 180.0;
        double projected = dx * Math.Cos(angleRad) + dy * Math.Sin(angleRad);
        return Math.Max(0, projected);
    }

    private static string FormatBendSegment(BendSegment bend, CultureInfo ci, bool isFirst, PhysicalPin? startPin = null)
    {
        var radius = bend.RadiusMicrometers.ToString("F2", ci);
        var sweepAngle = NazcaCoordinateMapper.NormalizeZero(-bend.SweepAngleDegrees).ToString("F2", ci);

        if (isFirst)
        {
            double nazcaX;
            double nazcaY;
            if (startPin != null)
            {
                // Anchor the chain on the pin's world position so the waveguide
                // starts exactly where the component's stub pin sits.
                (nazcaX, nazcaY) = NazcaCoordinateMapper.GetPinNazcaPosition(startPin);
            }
            else
            {
                (nazcaX, nazcaY) = NazcaCoordinateMapper.ToNazca(
                    bend.StartPoint.X, bend.StartPoint.Y);
            }

            var x = NazcaCoordinateMapper.NormalizeZero(nazcaX).ToString("F2", ci);
            var y = NazcaCoordinateMapper.NormalizeZero(nazcaY).ToString("F2", ci);
            var angle = NazcaCoordinateMapper.NormalizeZero(-bend.StartAngleDegrees).ToString("F2", ci);
            return $"        nd.bend(radius={radius}, angle={sweepAngle}).put({x}, {y}, {angle})";
        }

        return $"        nd.bend(radius={radius}, angle={sweepAngle}).put()";
    }

    /// <summary>
    /// Appends the pin-to-pin fallback for a connection/frozen path with no routed geometry
    /// (null or empty). Shared by live connections and zero-segment frozen paths (a group can
    /// freeze a connection that was never routed) so both render identically instead of a
    /// frozen path silently vanishing where a live one would fall back.
    /// </summary>
    private static void AppendFallbackExport(
        StringBuilder sb,
        PhysicalPin? startPin,
        PhysicalPin? endPin,
        Dictionary<Component, string> componentNames,
        MetalTraceStyle? metal = null)
    {
        if (startPin == null || endPin == null)
            return;

        // A routeless electrical connection is a direct metal straight between both pins on the
        // metal layer — the optical sbend interconnect (ic) would draw it as a waveguide (#682).
        if (metal != null)
        {
            sb.AppendLine(FormatStraightSegmentFromPins(startPin, endPin, metal));
            return;
        }

        var startRef = BuildEndpointReference(startPin, componentNames);
        var endRef = BuildEndpointReference(endPin, componentNames);

        if (startRef != null && endRef != null)
            sb.AppendLine($"        ic.sbend_p2p({startRef}, {endRef}).put()");
    }

    /// <summary>
    /// Builds the Nazca expression anchoring one connection endpoint for the p2p fallback.
    /// A PDK cell defines its own pin names which generally do NOT match the in-app names
    /// (KeyError at script run time), so its endpoint is anchored by absolute Nazca position
    /// and direction.
    /// </summary>
    private static string? BuildEndpointReference(
        PhysicalPin pin,
        Dictionary<Component, string> componentNames)
    {
        var component = pin.ParentComponent;
        if (component == null || !componentNames.TryGetValue(component, out _))
            return null;

        var ci = CultureInfo.InvariantCulture;
        var (x, y) = NazcaCoordinateMapper.GetPinNazcaPosition(pin);
        var px = x.ToString("F2", ci);
        var py = y.ToString("F2", ci);
        var pa = NazcaCoordinateMapper.GetPinNazcaAngle(pin).ToString("F0", ci);
        return $"({px}, {py}, {pa})";
    }

    private static void AppendFooter(StringBuilder sb)
    {
        sb.AppendLine("    return design");
        sb.AppendLine();
        sb.AppendLine("# Create and export the design");
        sb.AppendLine("design = create_design()");
        sb.AppendLine();
        sb.AppendLine("# Export GDS with filename matching this script");
        sb.AppendLine("import os");
        sb.AppendLine("import sys");
        sb.AppendLine("script_path = os.path.abspath(__file__)");
        sb.AppendLine("gds_filename = os.path.splitext(script_path)[0] + '.gds'");
        // topcells=[design]: plain nd.export_gds() would export the default 'nazca'
        // cell tree only, so the design had to be instantiated under it (design.put())
        // and the written GDS's sole top cell was the empty 'nazca' wrapper around
        // ConnectAPIC_Design — a re-import then offered only that wrapper as the
        // top-cell candidate and exploded into ONE black box instead of the placed
        // components. Exporting the design cell directly keeps it the GDS top cell.
        sb.AppendLine("nd.export_gds(topcells=[design], filename=gds_filename)");
        sb.AppendLine("print(f'GDS exported to: {gds_filename}')");
    }

    /// <summary>
    /// Maps a component to its Nazca function call string.
    /// Uses the stored NazcaFunctionName when it's a real PDK function,
    /// falls back to heuristic demofab mapping otherwise.
    /// </summary>
    internal static string GetNazcaFunction(Component comp)
    {
        // Use stored PDK function name if available and looks like a real function
        var funcName = comp.NazcaFunctionName;
        if (!string.IsNullOrEmpty(funcName) && NazcaCoordinateMapper.IsPdkFunction(funcName))
        {
            var funcParams = comp.NazcaFunctionParameters;
            if (!string.IsNullOrEmpty(funcParams)
                && NazcaCoordinateMapper.IsParametricStraight(funcName, funcParams))
            {
                // A parametric straight calls its generated stub — the same call
                // shape demo_pdk.* straights and non-dotted PDK straights already
                // use. The REAL module call (demo.shallow.strt) would dissolve at
                // export: nazca flattens interconnect straights into the PARENT
                // cell, erasing the component from the GDS structure (and merging
                // its two connections into one on re-import).
                var stubFuncName = System.Text.RegularExpressions.Regex.Replace(
                    funcName, @"[^a-zA-Z0-9_]", "_");
                return $"{stubFuncName}({funcParams})";
            }

            // Keep dots (for module attribute access like demo.mmi2x2_dp), replace other invalid chars.
            // The placement calls the parameter-specific stub (issue #783): StubName appends
            // the parameters hash exactly when the stub generator did — dotted names bypass
            // stubs (real module call) and parametric straights embed the length already.
            var pythonFuncName = System.Text.RegularExpressions.Regex.Replace(
                NazcaStubNaming.StubName(funcName, comp.NazcaFunctionParameters), @"[^a-zA-Z0-9_.]", "_");

            // Forward stored parameters verbatim — the caller (component model)
            // is responsible for ensuring they match the target PDK function's signature.
            if (!string.IsNullOrEmpty(funcParams))
                return $"{pythonFuncName}({funcParams})";
            else
                return $"{pythonFuncName}()";
        }

        // For demo_pdk components, sanitize the function name to a valid Python identifier (replace dots too)
        if (!string.IsNullOrEmpty(funcName) && funcName.StartsWith("demo_pdk.", StringComparison.OrdinalIgnoreCase))
        {
            var pythonFuncName = System.Text.RegularExpressions.Regex.Replace(funcName, @"[^a-zA-Z0-9_]", "_");

            // Skip parameters for stub components - stubs don't support them
            var funcParams = comp.NazcaFunctionParameters;
            bool isParametricStraight = NazcaCoordinateMapper.IsParametricStraight(funcName, funcParams);

            if (isParametricStraight && !string.IsNullOrEmpty(funcParams))
                return $"{pythonFuncName}({funcParams})";
            else
                return $"{pythonFuncName}()";
        }

        // Fallback: heuristic mapping to demofab
        var name = funcName?.ToLower() ?? comp.Identifier.ToLower();
        var ci = CultureInfo.InvariantCulture;

        if (name.Contains("straight") || name.Contains("waveguide"))
            return $"demo.shallow.strt(length={comp.WidthMicrometers.ToString(ci)})";
        if (name.Contains("splitter") || name.Contains("1x2"))
            return "demo.mmi1x2_sh()";
        if (name.Contains("grating"))
            return "demo.io()";
        if (name.Contains("coupler") || name.Contains("2x2"))
            return "demo.mmi2x2_dp()";
        if (name.Contains("phase") || name.Contains("shifter"))
            return "demo.eopm_dc(length=500)";
        if (name.Contains("detector") || name.Contains("photo"))
            return "demo.pd()";
        if (name.Contains("bend"))
            return "demo.shallow.bend(angle=90)";
        if (name.Contains("y-junction") || name.Contains("yjunction"))
            return "demo.mmi1x2_sh()";

        return $"demo.shallow.strt(length={comp.WidthMicrometers.ToString(ci)})";
    }
}
