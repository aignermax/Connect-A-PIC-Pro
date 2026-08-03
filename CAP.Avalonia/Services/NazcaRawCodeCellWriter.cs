using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CAP.Avalonia.Services.GdsFactoryExport.MixedBackend;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_Core.Components.PinKinds;
using CAP_Core.Export;

namespace CAP.Avalonia.Services;

/// <summary>
/// One raw-code component template planned for inlining into the Nazca export:
/// the resolved library template, the Python function name placements call, and
/// whether the template falls back to a box stub because its geometry source
/// (the .gds file its raw code loads) no longer exists.
/// </summary>
internal sealed class RawCodeExportEntry
{
    /// <summary>The library template carrying the nazca-backend <c>RawCode</c>.</summary>
    public required ComponentTemplate Template { get; init; }

    /// <summary>
    /// Python function name placements call (<c>component_&lt;sanitized template name&gt;</c>).
    /// The same name is used for the raw wrapper cell and, in fallback mode, for the
    /// box stub — the placement line is identical either way.
    /// </summary>
    public required string FunctionName { get; init; }

    /// <summary>
    /// True when the raw code's geometry source is missing (.gds deleted): the
    /// component exports as a placeholder box stub instead of the raw cell.
    /// </summary>
    public required bool IsFallback { get; init; }

    /// <summary>
    /// First placed component of this template — provides the pins and the
    /// <see cref="NazcaCoordinateMapper.GetStubAnchor"/> anchor for the wrapper cell,
    /// mirroring how the stub generator dedupes on its first placement.
    /// </summary>
    public required Component Representative { get; init; }
}

/// <summary>
/// The per-export raw-code inlining plan: which placed components resolve to a
/// nazca-backend raw-code template and which unique templates get their raw
/// module emitted. Built once per export by
/// <see cref="NazcaRawCodeCellWriter.BuildPlan"/> and consumed by the stub
/// generator (skips/falls back), the cell writer (emits wrappers) and the
/// placement writer (calls the wrapper) — one resolution, three consumers, so
/// the three can never disagree about what a component exports as.
/// </summary>
internal sealed class RawCodeExportPlan
{
    /// <summary>
    /// An empty plan for callers that pass no component library — identical to
    /// pre-inlining behavior. Deliberately per-call (not a shared static): the plan is
    /// mutable by design (<see cref="RawCodeEntries"/>, <see cref="Add"/>), so a shared
    /// instance would let one consumer corrupt another's "empty" plan.
    /// </summary>
    public static RawCodeExportPlan Empty => new();

    private readonly Dictionary<Component, RawCodeExportEntry> _entryByComponent = new();

    /// <summary>Unique raw-code (non-fallback) entries, in emission order.</summary>
    public List<RawCodeExportEntry> RawCodeEntries { get; } = new();

    /// <summary>Looks up the raw-code entry for a placed component.</summary>
    public bool TryGetEntry(Component comp, out RawCodeExportEntry entry) =>
        _entryByComponent.TryGetValue(comp, out entry!);

    /// <summary>Registers <paramref name="entry"/> for every placed component of its template.</summary>
    public void Add(Component comp, RawCodeExportEntry entry) => _entryByComponent[comp] = entry;
}

/// <summary>
/// Inlines nazca-backend raw-code components (GDS imports via <c>nd.load_gds</c>,
/// custom Python cells) into the export of <see cref="SimpleNazcaExporter"/>: each
/// unique template's raw module is emitted ONCE, wrapped in an aligned cell whose
/// origin sits at the geometry bbox bottom-left (Nazca Y-up) — the app-space bbox
/// top-left the placement math (<see cref="NazcaCoordinateMapper"/>'s zero-offset
/// fallback) anchors on. The wrapper declares the app's optical pins
/// (<c>nd.Pin</c>, same coordinate mapping as the stub cells) plus their port
/// labels, so exported waveguide routing lands on real pins and a re-import of the
/// result detects named pins (issue #808).
/// <para>
/// The raw cell is re-anchored by its own bbox at runtime
/// (<c>_raw.put(-_bb[0], -_bb[1])</c>), which is idempotent for snippets that
/// already align themselves (<c>GdsHierarchyImportSession</c>'s load_gds wrapper)
/// and the best generic interpretation of the "component box = geometry bbox" app
/// contract for arbitrary user cells. Nazca's bbox excludes TEXT records while the
/// app's GDS bbox includes label positions — the two can therefore differ by a
/// label overshoot; pins stay app-authoritative, so routing is unaffected.
/// </para>
/// </summary>
internal static class NazcaRawCodeCellWriter
{
    /// <summary>Backend tag of raw code that renders through the gdsfactory exporter, never here.</summary>
    private const string GdsFactoryBackendName = "gdsfactory";

    /// <summary>
    /// Extracts the file path of an <c>nd.load_gds(filename="…")</c> call, the way
    /// <c>GdsHierarchyImportSession</c> emits it. Only this machine-generated shape
    /// is probed for existence — arbitrary user raw code cannot be statically checked.
    /// </summary>
    private static readonly Regex LoadGdsFilenameRegex = new(
        @"load_gds\(\s*filename\s*=\s*""(?<path>(?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Resolves every exportable canvas component against <paramref name="library"/>
    /// and plans the raw-code inlining. Components whose template carries no
    /// nazca-backend raw code are absent from the plan (the exporter's legacy
    /// stub/heuristic paths keep handling them). A template whose raw code loads a
    /// .gds file that no longer exists becomes a fallback entry: it exports as a
    /// placeholder box stub and a warning is collected. A null/empty library yields
    /// <see cref="RawCodeExportPlan.Empty"/> — identical to pre-inlining behavior.
    /// </summary>
    /// <param name="canvas">The design canvas to export.</param>
    /// <param name="include">Optional partial-export predicate; null plans every component.</param>
    /// <param name="library">The loaded component library (raw-code lookup), or null.</param>
    /// <param name="exportWarnings">Optional collector for the missing-source fallback warnings.</param>
    public static RawCodeExportPlan BuildPlan(
        DesignCanvasViewModel canvas,
        Func<Component, bool>? include,
        IEnumerable<ComponentTemplate>? library,
        List<string>? exportWarnings)
    {
        var templates = library as IReadOnlyCollection<ComponentTemplate> ?? library?.ToList();
        if (templates is null || templates.Count == 0)
            return RawCodeExportPlan.Empty;

        var plan = new RawCodeExportPlan();
        var entryByTemplate = new Dictionary<ComponentTemplate, RawCodeExportEntry>();
        var usedFunctionNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var comp in EnumerateComponents(canvas, include))
        {
            var template = InherentBackendClassifier.ResolveTemplate(comp, templates);
            if (template is null || string.IsNullOrEmpty(template.RawCode)
                || string.Equals(template.RawCodeBackend, GdsFactoryBackendName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!entryByTemplate.TryGetValue(template, out var entry))
            {
                entry = CreateEntry(template, comp, usedFunctionNames, exportWarnings);
                entryByTemplate[template] = entry;
                if (!entry.IsFallback)
                    plan.RawCodeEntries.Add(entry);
            }
            plan.Add(comp, entry);
        }
        return plan;
    }

    /// <summary>
    /// Emits every planned raw-code cell: the raw module verbatim, then a wrapper
    /// that builds the raw cell ONCE at module level (so each wrapper captures the
    /// <c>component()</c> of ITS module — a later raw module redefines the name),
    /// re-anchors it to the bbox bottom-left, and declares the app's optical pins
    /// plus their port labels. Placements then call
    /// <c>component_&lt;name&gt;().put('org', …)</c> exactly like stub placements.
    /// <para>
    /// The capture is guarded by an identity check against a pre-module snapshot
    /// (<c>_prev_component</c>): a hand-rolled raw module that defines neither
    /// <c>component</c> nor <c>cell</c> can no longer silently inherit the PREVIOUS
    /// module's <c>component()</c> — it fails loudly on the undefined <c>cell</c>
    /// instead of exporting the wrong geometry.
    /// </para>
    /// </summary>
    public static void AppendCells(StringBuilder sb, RawCodeExportPlan plan, CultureInfo ci)
    {
        foreach (var entry in plan.RawCodeEntries)
        {
            var comp = entry.Representative;
            var func = entry.FunctionName;
            var (anchorX, anchorY) = NazcaCoordinateMapper.GetStubAnchor(comp);

            // Template Name/PdkSource are user-controlled: CR/LF would break out of the
            // generated comment/docstring (injecting raw script lines) and a literal
            // triple quote would terminate the docstring — strip those characters.
            var safeName = SimpleNazcaExporter.SanitizePythonComment(entry.Template.Name);
            var safePdk = SimpleNazcaExporter.SanitizePythonComment(entry.Template.PdkSource);
            sb.AppendLine($"# Raw-code component '{safeName}' (PDK '{safePdk}'): real geometry");
            sb.AppendLine("# from its raw-code cell, re-anchored to the geometry bbox bottom-left (Nazca Y-up),");
            sb.AppendLine("# the app-space bbox top-left the placement math anchors on.");
            // Snapshot before the module runs: the wrapper below must capture only a
            // component() THIS module (re)defined, never a previous module's stale one.
            sb.AppendLine("_prev_component = globals().get('component')");
            sb.AppendLine(entry.Template.RawCode!.TrimEnd());
            sb.AppendLine();
            sb.AppendLine($"_raw_{func} = component() if callable(globals().get('component')) and globals().get('component') is not _prev_component else cell");
            sb.AppendLine($"with nd.Cell(name='{func}') as _{func}_cell:");
            sb.AppendLine($"    \"\"\"Raw-code cell of {safeName}, bbox-aligned, with the app's pins.\"\"\"");
            sb.AppendLine($"    _bb = _raw_{func}.bbox");
            sb.AppendLine($"    _raw_{func}.put(-_bb[0], -_bb[1])");

            // Pins relative to the cell origin: the same (OffsetX-ox, oy-OffsetY)
            // mapping the stub cells use (NazcaCoordinateMapper.GetPinNazcaPosition
            // contract), so exported waveguides meet the raw cell's pins. nd.Pin is
            // an optical port — electrical pins are not emitted (#519).
            foreach (var pin in comp.PhysicalPins)
            {
                if (pin.MatterType != MatterType.Light) continue;

                var (uox, uoy) = NazcaCoordinateMapper.GetUnrotatedPinOffset(comp, pin);
                var px = NazcaCoordinateMapper.NormalizeZero(uox - anchorX).ToString("F2", ci);
                var py = NazcaCoordinateMapper.NormalizeZero(anchorY - uoy).ToString("F2", ci);
                var pa = NazcaCoordinateMapper.NormalizeZero(-pin.AngleDegrees).ToString("F0", ci);
                sb.AppendLine($"    nd.Pin('{pin.Name}').put({px}, {py}, {pa})");
                // Pin label at the same anchor — re-import detects the named pin there (#808).
                sb.AppendLine($"    nd.Annotation(text='{SimpleNazcaExporter.EscapePythonString(pin.Name)}', layer={SimpleNazcaExporter.PortLabelLayer}).put({px}, {py})");
            }

            sb.AppendLine();
            sb.AppendLine($"def {func}(**kwargs):");
            sb.AppendLine($"    return _{func}_cell");
            sb.AppendLine();
        }
    }

    /// <summary>
    /// Enumerates components exactly like the exporter's stub generator: canvas
    /// components with analysis tools skipped, groups flattened recursively. A full
    /// export (<paramref name="include"/> null) additionally skips components carrying
    /// a gdsfactory function — mirroring <see cref="SimpleNazcaExporter"/>'s placement
    /// loop, which renders them via the gdsfactory export instead. Without the mirror a
    /// nazca raw-code component that also carries a gdsfactory function would get a dead
    /// raw module + wrapper (its module-level <c>nd.load_gds</c> still runs!) plus a
    /// misleading missing-source warning and fallback stub. A partial export's include
    /// predicate alone decides (mixed-backend nazca components may carry both).
    /// </summary>
    private static IEnumerable<Component> EnumerateComponents(
        DesignCanvasViewModel canvas, Func<Component, bool>? include)
    {
        foreach (var compVm in canvas.Components)
        {
            var comp = compVm.Component;
            if (comp.IsAnalysisTool) continue;
            if (comp is ComponentGroup group)
            {
                foreach (var child in group.GetAllComponentsRecursive())
                {
                    if (child.IsAnalysisTool) continue;
                    if (include == null && !string.IsNullOrEmpty(child.GdsFactoryFunction)) continue;
                    if (include != null && !include(child)) continue;
                    yield return child;
                }
            }
            else
            {
                if (include == null && !string.IsNullOrEmpty(comp.GdsFactoryFunction)) continue;
                if (include != null && !include(comp)) continue;
                yield return comp;
            }
        }
    }

    /// <summary>
    /// Builds the plan entry for one unique template: a collision-free Python
    /// function name and the missing-source fallback decision. The fallback probes
    /// only the machine-generated <c>nd.load_gds(filename="…")</c> shape; other raw
    /// code is trusted (there is no static way to check it).
    /// </summary>
    private static RawCodeExportEntry CreateEntry(
        ComponentTemplate template,
        Component representative,
        HashSet<string> usedFunctionNames,
        List<string>? exportWarnings)
    {
        var baseName = "component_" + SanitizePythonIdentifier(template.Name);
        var functionName = baseName;
        for (var n = 2; !usedFunctionNames.Add(functionName); n++)
            functionName = $"{baseName}_{n}";

        var missingSource = FindMissingGdsSource(template.RawCode!);
        if (missingSource is not null)
        {
            exportWarnings?.Add(
                $"Component '{template.Name}' (PDK '{template.PdkSource}'): the GDS file " +
                $"'{missingSource}' its raw code loads no longer exists — exported as a " +
                "placeholder box. Restore the file or re-import the GDS for real geometry.");
        }

        return new RawCodeExportEntry
        {
            Template = template,
            FunctionName = functionName,
            IsFallback = missingSource is not null,
            Representative = representative,
        };
    }

    /// <summary>
    /// The path of the raw code's <c>nd.load_gds(filename="…")</c> when that file no
    /// longer exists, or null (no load_gds call, or the file is there). Escaped
    /// backslashes/quotes are unescaped the inverse way of
    /// <c>GdsCellDraftMapper.SubstituteGdsFileName</c>.
    /// </summary>
    private static string? FindMissingGdsSource(string rawCode)
    {
        var match = LoadGdsFilenameRegex.Match(rawCode);
        if (!match.Success)
            return null;

        var path = UnescapePythonString(match.Groups["path"].Value);
        return File.Exists(path) ? null : path;
    }

    /// <summary>Inverts the producer's Python string escaping (<c>\</c> → <c>\\</c>, <c>"</c> → <c>\"</c>).</summary>
    private static string UnescapePythonString(string escaped)
    {
        var sb = new StringBuilder(escaped.Length);
        for (var i = 0; i < escaped.Length; i++)
            sb.Append(escaped[i] == '\\' && i + 1 < escaped.Length ? escaped[++i] : escaped[i]);
        return sb.ToString();
    }

    /// <summary>Turns a template name into a valid Python identifier fragment; never empty.</summary>
    private static string SanitizePythonIdentifier(string name)
    {
        var sanitized = Regex.Replace(name ?? string.Empty, @"[^a-zA-Z0-9_]", "_");
        return sanitized.Length == 0 ? "raw" : sanitized;
    }
}
