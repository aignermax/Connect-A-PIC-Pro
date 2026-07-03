using System.Globalization;
using System.Text;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Export;
using CAP_Core.Export.InterconnectRouting;
using CAP_Core.Routing;
using CAP_Core.Routing.InterconnectRouting;
using CAP_DataAccess.Persistence.PIR;

namespace CAP.Avalonia.Services;

/// <summary>
/// Simple Nazca exporter for the physical coordinate system.
/// Exports components and waveguide connections to Python/Nazca code.
/// </summary>
public class SimpleNazcaExporter
{
    /// <summary>
    /// Optional source for global interconnect settings (width/radius/layer).
    /// When null, the historical defaults (<see cref="InterconnectSettings"/>) are used,
    /// keeping legacy output byte-identical.
    /// </summary>
    public Func<InterconnectSettings>? SettingsSource { get; set; }

    /// <summary>
    /// Exports the full design to a Python/Nazca script.
    /// </summary>
    /// <param name="canvas">The design canvas to export.</param>
    /// <param name="pdkModuleName">Optional PDK module name (e.g., "siepic_ebeam_pdk") for import.</param>
    /// <param name="overrides">
    /// Optional per-instance Nazca overrides keyed by component identifier (issue #559).
    /// For entries whose <see cref="NazcaCodeOverride.RawCode"/> is non-null, the export emits
    /// a self-contained factory cell and places the instance via that factory instead of the
    /// PDK template — org-anchored on the persisted bbox corner so the geometry lands on the
    /// component's grid rectangle. Connections to such instances export normally (issue #561).
    /// </param>
    /// <param name="emitVerification">
    /// When true, appends a machine-readable verification epilog (issue #565) that dumps
    /// every placed instance's ACTUAL world pin positions — reported by the same nazca
    /// engine that writes the GDS — to '&lt;script&gt;.pins.json' next to the script.
    /// </param>
    public string Export(
        DesignCanvasViewModel canvas,
        string? pdkModuleName = null,
        IReadOnlyDictionary<string, NazcaCodeOverride>? overrides = null,
        bool emitVerification = false)
    {
        var sb = new StringBuilder();

        // Build a flat map of overridden identifier -> RawCode (only non-null RawCode entries).
        var rawOverrides = BuildRawOverrides(overrides);

        var interconnectSettings = SettingsSource?.Invoke() ?? new InterconnectSettings();
        AppendHeader(sb, interconnectSettings);
        NazcaOverrideFactory.AppendFactories(sb, rawOverrides);
        AppendPdkComponentStubs(sb, canvas, rawOverrides);
        var componentNames = AppendComponents(sb, canvas, rawOverrides, overrides, emitVerification);
        AppendConnections(sb, canvas, componentNames, rawOverrides, interconnectSettings.GdsLayer);
        AppendFooter(sb);
        if (emitVerification)
            AppendVerificationEpilog(sb);

        return sb.ToString();
    }

    /// <summary>
    /// Reduces the full override map to a dictionary of identifier -> RawCode,
    /// keeping only entries whose RawCode is non-null and non-empty.
    /// </summary>
    private static Dictionary<string, string> BuildRawOverrides(
        IReadOnlyDictionary<string, NazcaCodeOverride>? overrides)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (overrides == null)
            return result;

        foreach (var kv in overrides)
        {
            if (!string.IsNullOrWhiteSpace(kv.Value?.RawCode))
                result[kv.Key] = kv.Value!.RawCode!;
        }
        return result;
    }

    private static void AppendHeader(StringBuilder sb, InterconnectSettings settings)
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
        sb.AppendLine("# Create interconnect for waveguide routing");
        sb.AppendLine(settings.GdsLayer.HasValue
            ? "ic = Interconnect(width=WG_WIDTH, radius=BEND_RADIUS, layer=WG_LAYER)"
            : "ic = Interconnect(width=WG_WIDTH, radius=BEND_RADIUS)");
        sb.AppendLine();
    }

    /// <summary>
    /// Generates standalone Nazca cell definitions for PDK components.
    /// Each unique PDK function used in the design gets a stub cell
    /// with correct dimensions and pin positions — no external PDK install needed.
    /// ComponentGroups are flattened — stubs are generated for all child components.
    /// </summary>
    private static void AppendPdkComponentStubs(
        StringBuilder sb, DesignCanvasViewModel canvas, IReadOnlyDictionary<string, string> rawOverrides)
    {
        var ci = CultureInfo.InvariantCulture;
        var generated = new HashSet<string>(StringComparer.Ordinal);

        foreach (var compVm in canvas.Components)
        {
            var comp = compVm.Component;
            if (comp.IsAnalysisTool) continue;
            if (comp is ComponentGroup group)
            {
                foreach (var child in group.GetAllComponentsRecursive())
                {
                    if (child.IsAnalysisTool) continue;
                    // Overridden instances are self-defined by their factory; a PDK stub
                    // would be unused / could conflict, so skip it (issue #559).
                    if (rawOverrides.ContainsKey(child.Identifier)) continue;
                    AppendComponentStub(sb, child, generated, ci);
                }
            }
            else
            {
                if (rawOverrides.ContainsKey(comp.Identifier)) continue;
                AppendComponentStub(sb, comp, generated, ci);
            }
        }
    }

    /// <summary>
    /// Generates a PDK stub for a single component if required.
    /// </summary>
    private static void AppendComponentStub(
        StringBuilder sb, Component comp, HashSet<string> generated, CultureInfo ci)
    {
        var funcName = comp.NazcaFunctionName;
        if (string.IsNullOrEmpty(funcName) || !RequiresStub(funcName))
            return;
        if (!generated.Add(funcName))
            return;

        if (NazcaCoordinateMapper.IsParametricStraight(funcName, comp.NazcaFunctionParameters))
            AppendParametricStraightStub(sb, funcName, comp, ci);
        else
            AppendStandardComponentStub(sb, funcName, comp, ci);
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
        var (_, anchorY) = NazcaCoordinateMapper.GetStubAnchor(comp);
        var firstPin = comp.PhysicalPins.FirstOrDefault();
        var firstPinY = firstPin != null
            ? NazcaCoordinateMapper.GetUnrotatedPinOffset(comp, firstPin).OffsetY
            : 0;
        var strtY = NazcaCoordinateMapper.NormalizeZero(anchorY - firstPinY).ToString("F2", ci);

        // Sanitize function name for valid Python identifier (replace non-alphanumeric/underscore chars)
        var pythonFuncName = System.Text.RegularExpressions.Regex.Replace(funcName, @"[^a-zA-Z0-9_]", "_");

        sb.AppendLine($"def {pythonFuncName}(length=100, **kwargs):");
        sb.AppendLine($"    \"\"\"Auto-generated parametric straight waveguide stub for {funcName}.\"\"\"");
        sb.AppendLine($"    with nd.Cell(name='{funcName}_{{length}}') as cell:");
        sb.AppendLine($"        # Use nd.strt() for proper waveguide with specified length");
        sb.AppendLine($"        nd.strt(length=length, width=0.45, layer=1).put(0, {strtY})");

        // Generate pins from the UNROTATED offsets, relative to org (the mapper anchor);
        // a straight's pins share the centre line, so their local Y is oy - OffsetY = 0.
        foreach (var pin in comp.PhysicalPins)
        {
            var (uox, uoy) = NazcaCoordinateMapper.GetUnrotatedPinOffset(comp, pin);
            var py = NazcaCoordinateMapper.NormalizeZero(anchorY - uoy).ToString("F2", ci);
            var pa = NazcaCoordinateMapper.NormalizeZero(-pin.AngleDegrees).ToString("F0", ci);

            // For straight waveguides: input pin at x=0, output pin at x=length.
            if (uox == 0)
                sb.AppendLine($"        nd.Pin('{pin.Name}').put(0, {py}, {pa})");
            else
                sb.AppendLine($"        nd.Pin('{pin.Name}').put(length, {py}, {pa})");
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
    /// </summary>
    private static void AppendStandardComponentStub(
        StringBuilder sb, string funcName, Component comp, CultureInfo ci)
    {
        var w = comp.WidthMicrometers;
        var h = comp.HeightMicrometers;

        // Sanitize function name for valid Python identifier (replace non-alphanumeric/underscore chars)
        var pythonFuncName = System.Text.RegularExpressions.Regex.Replace(funcName, @"[^a-zA-Z0-9_]", "_");

        // Define cell once, return cached instance on each call
        sb.AppendLine($"with nd.Cell(name='{funcName}') as _{pythonFuncName}_cell:");
        sb.AppendLine($"    \"\"\"Auto-generated stub for {funcName} ({comp.WidthMicrometers}x{comp.HeightMicrometers} µm).\"\"\"");

        // Stubs are only generated for PDK-named components (see RequiresStub), whose
        // placement always uses the calibrated origin offset — (0,0) means org at the
        // box top-left. Example: GC with offset (0, 9.5), H=19 → polygon (0,-9.5)..(W,9.5).
        double offsetX = comp.NazcaOriginOffsetX;
        double offsetY = comp.NazcaOriginOffsetY;

        var px0 = NazcaCoordinateMapper.NormalizeZero(-offsetX).ToString("F2", ci);
        var py0 = NazcaCoordinateMapper.NormalizeZero(offsetY - h).ToString("F2", ci);
        var px1 = NazcaCoordinateMapper.NormalizeZero(w - offsetX).ToString("F2", ci);
        var py1 = NazcaCoordinateMapper.NormalizeZero(offsetY).ToString("F2", ci);

        sb.AppendLine($"    nd.Polygon(points=[({px0},{py0}),({px1},{py0}),({px1},{py1}),({px0},{py1})], layer=1).put(0, 0)");

        // Pins relative to org: local = (OffsetX-ox, oy-OffsetY), the plain Y negation
        // of the app pin offsets (NazcaCoordinateMapper.GetPinNazcaPosition contract).
        foreach (var pin in comp.PhysicalPins)
        {
            var px = NazcaCoordinateMapper.NormalizeZero(pin.OffsetXMicrometers - offsetX).ToString("F2", ci);
            var py = NazcaCoordinateMapper.NormalizeZero(offsetY - pin.OffsetYMicrometers).ToString("F2", ci);
            var pa = NazcaCoordinateMapper.NormalizeZero(-pin.AngleDegrees).ToString("F0", ci);
            sb.AppendLine($"    nd.Pin('{pin.Name}').put({px}, {py}, {pa})");
        }

        sb.AppendLine();
        sb.AppendLine($"def {pythonFuncName}(**kwargs):");
        sb.AppendLine($"    return _{pythonFuncName}_cell");
        sb.AppendLine();
    }

    private static Dictionary<Component, string> AppendComponents(
        StringBuilder sb, DesignCanvasViewModel canvas, IReadOnlyDictionary<string, string> rawOverrides,
        IReadOnlyDictionary<string, NazcaCodeOverride>? overrides, bool emitVerification = false)
    {
        sb.AppendLine("def create_design():");
        sb.AppendLine("    with nd.Cell(name='ConnectAPIC_Design') as design:");
        sb.AppendLine();
        sb.AppendLine("        # Components");
        var componentNames = new Dictionary<Component, string>();
        int compIndex = 0;
        var ci = CultureInfo.InvariantCulture;

        foreach (var compVm in canvas.Components)
        {
            var comp = compVm.Component;
            if (comp.IsAnalysisTool) continue;
            if (comp is ComponentGroup group)
            {
                // Flatten group: export all child components at their absolute positions
                foreach (var child in group.GetAllComponentsRecursive())
                {
                    if (child.IsAnalysisTool) continue;
                    AppendSingleComponent(sb, child, componentNames, ref compIndex, ci, rawOverrides, overrides);
                }
            }
            else
            {
                AppendSingleComponent(sb, comp, componentNames, ref compIndex, ci, rawOverrides, overrides);
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
        ref int compIndex, CultureInfo ci, IReadOnlyDictionary<string, string> rawOverrides,
        IReadOnlyDictionary<string, NazcaCodeOverride>? overrides)
    {
        var varName = $"comp_{compIndex}";

        bool isRawOverride = rawOverrides.ContainsKey(comp.Identifier);

        // A raw-code override persists the cell-internal bbox anchor (XMin, YMax) so
        // the mapper can land the rendered geometry on the component's grid rectangle.
        // Overrides saved before the anchor fields existed leave the anchor null.
        (double XMin, double YMax)? overrideAnchor = null;
        if (isRawOverride && overrides != null
            && overrides.TryGetValue(comp.Identifier, out var overrideRecord)
            && overrideRecord.OverrideBboxXMinMicrometers is { } anchorXMin
            && overrideRecord.OverrideBboxYMaxMicrometers is { } anchorYMax)
        {
            overrideAnchor = (anchorXMin, anchorYMax);
        }

        var placement = NazcaCoordinateMapper.GetCellPlacement(comp, overrideAnchor);
        var nazcaX = placement.X.ToString("F2", ci);
        var nazcaY = placement.Y.ToString("F2", ci);
        var rot = placement.RotationDegrees.ToString("F0", ci);
        var nazcaFunc = GetNazcaFunction(comp);

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
        if (isRawOverride)
        {
            var factory = NazcaOverrideFactory.FactoryName(comp.Identifier);
            // With a persisted bbox anchor the cell is org-anchored so its geometry
            // lands exactly on the grid rectangle (issue #561). Every nd.Cell() has
            // an 'org' pin. Overrides saved before the anchor existed fall back to
            // the old default-anchor placement.
            sb.AppendLine(overrideAnchor != null
                ? $"        {varName} = {factory}().put('org', {nazcaX}, {nazcaY}, {rot})  # {comp.Identifier} (raw-code override, bbox-anchored)"
                : $"        {varName} = {factory}().put({nazcaX}, {nazcaY}, {rot})  # {comp.Identifier} (raw-code override)");
        }
        else
        {
            sb.AppendLine($"        {varName} = {nazcaFunc}.put('org', {nazcaX}, {nazcaY}, {rot})  # {comp.Identifier}");
        }

        // Record the variable only after its put-line was emitted: a half-failed append
        // must not leave a name pointing at a component that was never placed.
        componentNames[comp] = varName;
        compIndex++;
    }

    private static void AppendConnections(
        StringBuilder sb,
        DesignCanvasViewModel canvas,
        Dictionary<Component, string> componentNames,
        IReadOnlyDictionary<string, string> rawOverrides,
        int? gdsLayer = null)
    {
        var hasFrozenPaths = canvas.Components.Any(vm => vm.Component is ComponentGroup);
        if (canvas.Connections.Count == 0 && !hasFrozenPaths)
            return;

        sb.AppendLine("        # Waveguide Connections");

        foreach (var connVm in canvas.Connections)
        {
            var conn = connVm.Connection;
            // Skip connections that touch a virtual analysis tool — those pins
            // have no physical fab counterpart.
            if (conn.StartPin?.ParentComponent?.IsAnalysisTool == true) continue;
            if (conn.EndPin?.ParentComponent?.IsAnalysisTool == true) continue;

            // Issue #561: connections touching raw-code–overridden instances export
            // their REAL routed segments like any other connection — the override
            // cell is bbox-anchored, so app-space segment coordinates line up with
            // its geometry and the GDS shows the same bends/radii as the canvas.
            // Only routeless connections fall back to a p2p interconnect.
            // Explicit routing style (issue #574): export a single Nazca primitive
            // (strt/sinebend/bend/euler/cobra) instead of the routed segments.
            var styledLine = NazcaConnectionStyleWriter.Format(conn, gdsLayer);
            if (styledLine != null)
            {
                sb.AppendLine(styledLine);
                continue;
            }

            var segments = conn.GetPathSegments();

            if (segments.Count > 0)
                AppendSegmentExport(sb, segments, conn.StartPin, conn.EndPin);
            else
                AppendFallbackExport(sb, conn, componentNames, rawOverrides);
        }

        // Export frozen waveguide paths from ComponentGroups
        foreach (var compVm in canvas.Components)
        {
            if (compVm.Component is ComponentGroup group)
                AppendGroupFrozenPaths(sb, group);
        }

        sb.AppendLine();
    }

    /// <summary>
    /// Exports all frozen waveguide paths from a ComponentGroup (and nested groups) as Nazca segments.
    /// </summary>
    private static void AppendGroupFrozenPaths(StringBuilder sb, ComponentGroup group)
    {
        foreach (var frozenPath in group.InternalPaths)
        {
            if (frozenPath?.Path?.Segments?.Count > 0)
                AppendSegmentExport(sb, frozenPath.Path.Segments, frozenPath.StartPin, frozenPath.EndPin);
        }

        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup nestedGroup)
                AppendGroupFrozenPaths(sb, nestedGroup);
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
        PhysicalPin? startPin = null, PhysicalPin? endPin = null)
    {
        // Single straight segment: compute geometry directly from both pin positions
        // so the waveguide hits both pins exactly even if the stored segment drifts.
        if (segments.Count == 1 && segments[0] is StraightSegment && startPin != null && endPin != null)
        {
            sb.AppendLine(FormatStraightSegmentFromPins(startPin, endPin));
            return;
        }

        foreach (var segment in segments)
        {
            var (nStartX, nStartY) = NazcaCoordinateMapper.ToNazca(segment.StartPoint.X, segment.StartPoint.Y);
            var (nEndX, nEndY) = NazcaCoordinateMapper.ToNazca(segment.EndPoint.X, segment.EndPoint.Y);

            sb.AppendLine(FormatSegmentAbsolute(segment, nStartX, nStartY, nEndX, nEndY));
        }
    }

    /// <summary>
    /// Formats a path segment (straight or bend) with absolute Nazca positions.
    /// Straight segments compute length and angle from the transformed Nazca endpoints,
    /// ensuring the exported geometry matches the actual endpoint positions.
    /// Bend segments use stored radius/sweep with negated angles for Y-flip.
    /// </summary>
    private static string FormatSegmentAbsolute(
        PathSegment segment, double nazcaStartX, double nazcaStartY,
        double nazcaEndX, double nazcaEndY)
    {
        var ci = CultureInfo.InvariantCulture;
        return segment switch
        {
            StraightSegment => FormatStraightAbsolute(
                nazcaStartX, nazcaStartY, nazcaEndX, nazcaEndY, ci),
            BendSegment bend => FormatBendAbsolute(bend, nazcaStartX, nazcaStartY, ci),
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
        double nazcaEndX, double nazcaEndY, CultureInfo ci)
    {
        double dx = nazcaEndX - nazcaStartX;
        double dy = nazcaEndY - nazcaStartY;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double angleDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;

        var l = length.ToString("F2", ci);
        var x = NazcaCoordinateMapper.NormalizeZero(nazcaStartX).ToString("F2", ci);
        var y = NazcaCoordinateMapper.NormalizeZero(nazcaStartY).ToString("F2", ci);
        var a = NazcaCoordinateMapper.NormalizeZero(angleDeg).ToString("F2", ci);
        return $"        nd.strt(length={l}).put({x}, {y}, {a})";
    }

    /// <summary>
    /// Formats a bend segment with absolute Nazca start position.
    /// The radius is invariant under Y-flip; the sweep angle and start angle are negated.
    /// </summary>
    private static string FormatBendAbsolute(
        BendSegment bend, double nazcaX, double nazcaY, CultureInfo ci)
    {
        var radius = bend.RadiusMicrometers.ToString("F2", ci);
        var sweepAngle = NazcaCoordinateMapper.NormalizeZero(-bend.SweepAngleDegrees).ToString("F2", ci);
        var x = NazcaCoordinateMapper.NormalizeZero(nazcaX).ToString("F2", ci);
        var y = NazcaCoordinateMapper.NormalizeZero(nazcaY).ToString("F2", ci);
        var angle = NazcaCoordinateMapper.NormalizeZero(-bend.StartAngleDegrees).ToString("F2", ci);
        return $"        nd.bend(radius={radius}, angle={sweepAngle}).put({x}, {y}, {angle})";
    }

    /// <summary>
    /// Formats a straight waveguide segment using absolute Nazca pin positions.
    /// Computes length and angle from start pin to end pin in Nazca coordinates,
    /// ensuring the waveguide reaches both pins exactly.
    /// </summary>
    private static string FormatStraightSegmentFromPins(PhysicalPin startPin, PhysicalPin endPin)
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

        return $"        nd.strt(length={l}).put({x}, {y}, {a})";
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

    private static void AppendFallbackExport(
        StringBuilder sb,
        WaveguideConnection conn,
        Dictionary<Component, string> componentNames,
        IReadOnlyDictionary<string, string> rawOverrides)
    {
        var startRef = BuildEndpointReference(conn.StartPin, componentNames, rawOverrides);
        var endRef = BuildEndpointReference(conn.EndPin, componentNames, rawOverrides);

        if (startRef != null && endRef != null)
            sb.AppendLine($"        ic.sbend_p2p({startRef}, {endRef}).put()");
    }

    /// <summary>
    /// Builds the Nazca expression anchoring one connection endpoint for the p2p fallback.
    /// A raw-code–overridden instance exposes the in-app pin names on its cell, so a pin
    /// reference (<c>comp_N.pin['name']</c>) is exact. A regular PDK cell defines its own
    /// pin names which generally do NOT match the in-app names (KeyError at script run
    /// time), so its endpoint is anchored by absolute Nazca position and direction instead.
    /// </summary>
    private static string? BuildEndpointReference(
        PhysicalPin pin,
        Dictionary<Component, string> componentNames,
        IReadOnlyDictionary<string, string> rawOverrides)
    {
        var component = pin.ParentComponent;
        if (component == null || !componentNames.TryGetValue(component, out var name))
            return null;

        bool isOverridden = component.Identifier != null
            && rawOverrides.ContainsKey(component.Identifier);
        if (isOverridden)
            return $"{name}.pin['{pin.Name}']";

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
        sb.AppendLine("design.put()");
        sb.AppendLine();
        sb.AppendLine("# Export GDS with filename matching this script");
        sb.AppendLine("import os");
        sb.AppendLine("import sys");
        sb.AppendLine("script_path = os.path.abspath(__file__)");
        sb.AppendLine("gds_filename = os.path.splitext(script_path)[0] + '.gds'");
        sb.AppendLine("nd.export_gds(filename=gds_filename)");
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
            // Keep dots (for module attribute access like demo.mmi2x2_dp), replace other invalid chars
            var pythonFuncName = System.Text.RegularExpressions.Regex.Replace(funcName, @"[^a-zA-Z0-9_.]", "_");

            // Forward stored parameters verbatim — the caller (component model)
            // is responsible for ensuring they match the target PDK function's signature.
            var funcParams = comp.NazcaFunctionParameters;
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
