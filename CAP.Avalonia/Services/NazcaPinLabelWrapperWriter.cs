using System.Globalization;
using System.Text;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Core;
using CAP_Core.Components.PinKinds;
using CAP_Core.Export;

namespace CAP.Avalonia.Services;

/// <summary>
/// One pin-label wrapper cell of a <see cref="PinLabelWrapperPlan"/>: a thin
/// nazca cell that places the real PDK cell (<see cref="InnerCall"/>) at its
/// 'org' anchor and adds the application's pin labels on the port-label layer.
/// </summary>
internal sealed record PinLabelWrapperEntry
{
    /// <summary>GDS/nazca cell name — the component's display (template) name, so re-import resolves it back to the library template.</summary>
    public string CellName { get; init; } = string.Empty;

    /// <summary>Python function name the placement calls (sanitized, unique per export).</summary>
    public string FunctionName { get; init; } = string.Empty;

    /// <summary>The real PDK call placed inside the wrapper (e.g. <c>demo.pd()</c>).</summary>
    public string InnerCall { get; init; } = string.Empty;

    /// <summary>Component docstring summary (width × height, sanitized for Python).</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Pin labels: name + wrapper-local nazca position (the stub-anchor mapping).</summary>
    public IReadOnlyList<(string Name, double XUm, double YUm)> Labels { get; init; } =
        Array.Empty<(string, double, double)>();
}

/// <summary>
/// Per-export plan of pin-label wrappers: maps each eligible placed component
/// to the wrapper call its placement line must use.
/// </summary>
internal sealed class PinLabelWrapperPlan
{
    /// <summary>An empty plan (no wrappers) — per-call, like <see cref="RawCodeExportPlan.Empty"/>.</summary>
    public static PinLabelWrapperPlan Empty => new();

    private readonly Dictionary<Component, PinLabelWrapperEntry> _entryByComponent = new();

    /// <summary>Unique wrapper entries, in emission order.</summary>
    public List<PinLabelWrapperEntry> Entries { get; } = new();

    /// <summary>Looks up the wrapper entry for a placed component.</summary>
    public bool TryGetEntry(Component comp, out PinLabelWrapperEntry entry) =>
        _entryByComponent.TryGetValue(comp, out entry!);

    /// <summary>Registers <paramref name="entry"/> for <paramref name="comp"/>.</summary>
    public void Add(Component comp, PinLabelWrapperEntry entry) => _entryByComponent[comp] = entry;
}

/// <summary>
/// Wraps real-module-called components that carry ELECTRICAL pins (the bundled
/// demofab parts: <c>demo.pd</c>, <c>demo.eopm_dc</c>, …) in a thin cell that
/// re-exposes the application's pins as port-label TEXTs. Rationale: the export
/// places the REAL demofab cells, whose own pin labels live on demofab's
/// black-box layer (501, 1) with demofab's pin names/positions — the app's
/// electrical pins (a detector's anode/cathode) exist nowhere in that geometry,
/// so a re-import could never see the metal traces' endpoints. The wrapper cell
/// is named after the component's display (template) name and labels EVERY app
/// pin (optical ones too — one authoritative label set per component, anchored
/// exactly on the app pin positions via the
/// <see cref="NazcaCoordinateMapper.GetStubAnchor"/> mapping), so the GDS
/// re-import resolves the cell straight back to the library template with
/// kind-correct pins. Geometry is untouched: the wrapper adds only TEXT records
/// plus the 'org' anchor pin. Components exported through stubs (SiEPIC
/// <c>ebeam_*</c>, <c>demo_pdk.*</c>) don't need this — the stubs label their
/// pins directly.
/// </summary>
internal static class NazcaPinLabelWrapperWriter
{
    /// <summary>
    /// Collects the wrapper plan for <paramref name="canvas"/>: every placed
    /// component with at least one electrical pin whose placement calls a real
    /// module function (dotted nazca name, e.g. demofab) gets one entry per
    /// unique (display name, call, pin set) — identical components share a cell,
    /// a genuine content difference under one name gets a deterministic
    /// <c>_2</c> suffix (which then deliberately no longer matches a template
    /// name on re-import — ambiguous content must not resolve silently).
    /// </summary>
    /// <param name="canvas">The design canvas.</param>
    /// <param name="include">Optional group filter of a partial (mixed-backend) export.</param>
    /// <param name="rawCodePlan">
    /// The raw-code inlining plan: components it covers render their real
    /// geometry through raw-code wrappers (which already label their pins) and
    /// are skipped here.
    /// </param>
    public static PinLabelWrapperPlan BuildPlan(
        DesignCanvasViewModel canvas, Func<Component, bool>? include, RawCodeExportPlan? rawCodePlan)
    {
        var plan = new PinLabelWrapperPlan();
        var takenNames = new Dictionary<string, string>(StringComparer.Ordinal); // cell name → content signature

        foreach (var compVm in canvas.Components)
        {
            var comp = compVm.Component;
            if (comp.IsAnalysisTool) continue;
            if (comp is ComponentGroup group)
            {
                foreach (var child in group.GetAllComponentsRecursive())
                {
                    if (!child.IsAnalysisTool)
                        AddIfEligible(plan, takenNames, child, include, rawCodePlan);
                }
            }
            else
            {
                AddIfEligible(plan, takenNames, comp, include, rawCodePlan);
            }
        }
        return plan;
    }

    /// <summary>Emits the wrapper cell definitions + accessor functions.</summary>
    public static void AppendCells(StringBuilder sb, PinLabelWrapperPlan plan)
    {
        var ci = CultureInfo.InvariantCulture;
        foreach (var entry in plan.Entries)
        {
            var cellVar = $"_{entry.FunctionName}_cell";
            sb.AppendLine($"with nd.Cell(name='{SimpleNazcaExporter.EscapePythonString(entry.CellName)}') as {cellVar}:");
            sb.AppendLine($"    \"\"\"Pin-label wrapper around {entry.InnerCall} ({entry.Summary}).\"\"\"");
            sb.AppendLine($"    {entry.InnerCall}.put('org', 0, 0, 0)");
            // No own 'org' pin: a pin-less cell's put('org', x, y, rot) anchors the
            // cell origin with the requested rotation — the same fallback contract
            // the generated stub cells rely on (an explicit nd.Pin('org') would
            // instead engage nazca's pin-angle anchoring and flip the placement).
            foreach (var (name, x, y) in entry.Labels)
            {
                sb.AppendLine(
                    $"    nd.Annotation(text='{SimpleNazcaExporter.EscapePythonString(name)}', " +
                    $"layer={SimpleNazcaExporter.PortLabelLayer}).put({x.ToString("F2", ci)}, {y.ToString("F2", ci)})");
            }
            sb.AppendLine();
            sb.AppendLine($"def {entry.FunctionName}(**kwargs):");
            sb.AppendLine($"    return {cellVar}");
            sb.AppendLine();
        }
    }

    private static void AddIfEligible(
        PinLabelWrapperPlan plan,
        Dictionary<string, string> takenNames,
        Component comp,
        Func<Component, bool>? include,
        RawCodeExportPlan? rawCodePlan)
    {
        if (include != null && !include(comp)) return;
        // gdsfactory-native components are skipped by the nazca export entirely
        // (full export only — an include predicate partitions on its own).
        if (include == null && !string.IsNullOrEmpty(comp.GdsFactoryFunction)) return;
        // Raw-code components render real geometry with their own pin labels.
        if (rawCodePlan is not null && rawCodePlan.TryGetEntry(comp, out _)) return;
        if (!comp.PhysicalPins.Any(PinKindHelper.IsElectrical)) return;

        var funcName = comp.NazcaFunctionName;
        if (string.IsNullOrEmpty(funcName)) return;
        // Only real module calls (dotted names, e.g. demo.pd) lose their app pins
        // in the export; stub-called functions (ebeam_*, demo_pdk.*, parametric
        // straights) label their pins inside the generated stubs already.
        if (!NazcaCoordinateMapper.IsPdkFunction(funcName)
            || !funcName.Contains('.', StringComparison.Ordinal)
            || NazcaCoordinateMapper.IsParametricStraight(funcName, comp.NazcaFunctionParameters))
            return;

        var innerCall = SimpleNazcaExporter.GetNazcaFunction(comp);
        var ci = CultureInfo.InvariantCulture;
        var (anchorX, anchorY) = NazcaCoordinateMapper.GetStubAnchor(comp);
        var labels = comp.PhysicalPins
            .Select(pin =>
            {
                var (uox, uoy) = NazcaCoordinateMapper.GetUnrotatedPinOffset(comp, pin);
                return (pin.Name,
                    NazcaCoordinateMapper.NormalizeZero(uox - anchorX),
                    NazcaCoordinateMapper.NormalizeZero(anchorY - uoy));
            })
            .ToList();
        string signature = string.Join("|", new[] { innerCall }
            .Concat(labels.Select(l => $"{l.Item1}:{l.Item2.ToString(ci)}:{l.Item3.ToString(ci)}")));

        var baseName = GdsImport.GdsTemplateResolver.SanitizeGdsCellName(
            string.IsNullOrWhiteSpace(comp.HumanReadableName) ? funcName : comp.HumanReadableName);
        string cellName = baseName;
        for (var n = 2; takenNames.TryGetValue(cellName, out var existing) && existing != signature; n++)
            cellName = $"{baseName}_{n}";

        if (takenNames.TryGetValue(cellName, out var same)
            && same == signature
            && plan.Entries.FirstOrDefault(e => e.CellName == cellName) is { } existingEntry)
        {
            plan.Add(comp, existingEntry);
            return;
        }

        var entry = new PinLabelWrapperEntry
        {
            CellName = cellName,
            FunctionName = $"lunima_pinwrap_{System.Text.RegularExpressions.Regex.Replace(cellName, @"[^a-zA-Z0-9_]", "_")}",
            InnerCall = innerCall,
            Summary = SimpleNazcaExporter.SanitizePythonComment(
                $"{comp.WidthMicrometers.ToString(ci)}x{comp.HeightMicrometers.ToString(ci)} µm"),
            Labels = labels,
        };
        takenNames[cellName] = signature;
        plan.Entries.Add(entry);
        plan.Add(comp, entry);
    }
}
