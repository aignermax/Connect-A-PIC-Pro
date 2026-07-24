using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_Core.Routing.MetalRouting;

namespace CAP.Avalonia.Services.GdsFactoryExport.MixedBackend;

/// <summary>The two scripts of a mixed-backend export.</summary>
/// <param name="GdsFactoryScript">Main gdsfactory script: gdsfactory-native placements, all
/// routed connections, and the merge of the nazca partial GDS into one output GDS.</param>
/// <param name="NazcaPartialScript">Nazca script rendering only the nazca-native placements.</param>
public sealed record MixedBackendScriptSet(string GdsFactoryScript, string NazcaPartialScript);

/// <summary>
/// Orchestrates the mixed-backend GDS export: placements are grouped by their
/// component's INHERENT backend (<see cref="InherentBackendClassifier"/>), the nazca-native
/// group is rendered by <see cref="SimpleNazcaExporter"/> into a partial GDS, and the main
/// gdsfactory script renders its own group, imports the partial via <c>gf.import_gds()</c>,
/// and writes the single merged output GDS. Routed connections are owned by the gdsfactory
/// script exclusively, so no geometry is emitted twice.
/// </summary>
public class MixedBackendGdsOrchestrator
{
    /// <summary>Top cell name of the nazca partial — distinct from the gdsfactory design
    /// cell ('ConnectAPIC_Design') so <c>gf.import_gds()</c> cannot collide on it.</summary>
    public const string NazcaPartialTopCellName = "ConnectAPIC_NazcaPartial";

    private const string PartialSuffix = "_nazca_partial";

    private readonly SimpleNazcaExporter _nazcaExporter;
    private readonly GdsFactoryExporter _gdsFactoryExporter;

    /// <summary>Initializes the orchestrator with the two backend exporters.</summary>
    /// <param name="nazcaExporter">Nazca script generator (carries interconnect settings).</param>
    /// <param name="gdsFactoryExporter">gdsfactory script generator; a default one when null.</param>
    public MixedBackendGdsOrchestrator(
        SimpleNazcaExporter? nazcaExporter = null, GdsFactoryExporter? gdsFactoryExporter = null)
    {
        _nazcaExporter = nazcaExporter ?? new SimpleNazcaExporter();
        _gdsFactoryExporter = gdsFactoryExporter ?? new GdsFactoryExporter();
    }

    /// <summary>
    /// True when the design mixes both inherent backends: at least one gdsfactory-native
    /// and at least one nazca-native exportable component. Only then is the two-script
    /// merge export used; single-backend designs keep their existing export path.
    /// </summary>
    /// <param name="canvas">The design canvas.</param>
    /// <param name="library">The loaded component library (raw-code backend lookup).</param>
    public static bool IsMixedBackendDesign(
        DesignCanvasViewModel canvas, IEnumerable<ComponentTemplate> library)
    {
        var templates = library.ToList();
        var backends = EnumerateExportableComponents(canvas)
            .Select(c => InherentBackendClassifier.Classify(c, templates))
            .Distinct()
            .Take(2)
            .ToList();
        return backends.Count == 2;
    }

    /// <summary>The path of the nazca partial script belonging to a main export script —
    /// same directory, <c>&lt;stem&gt;_nazca_partial.py</c>. The partial GDS lands next to it
    /// (same stem, <c>.gds</c> — the shared script-runner convention).</summary>
    /// <param name="mainScriptPath">The user-chosen main export script path.</param>
    public static string PartialScriptPathFor(string mainScriptPath) =>
        Path.Combine(
            Path.GetDirectoryName(mainScriptPath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(mainScriptPath) + PartialSuffix + ".py");

    /// <summary>
    /// Builds both export scripts for a mixed-backend design. The caller writes the partial
    /// script to <see cref="PartialScriptPathFor"/>, runs it FIRST (it produces the partial
    /// GDS), then runs the main script which merges and writes the final GDS.
    /// </summary>
    /// <param name="canvas">The design canvas.</param>
    /// <param name="options">Component representation mode for the gdsfactory group.</param>
    /// <param name="metalSpec">Process-derived metal routing parameters, or null for defaults.</param>
    /// <param name="library">The loaded component library (raw-code backend lookup).</param>
    /// <param name="mainScriptPath">The user-chosen main export script path.</param>
    public MixedBackendScriptSet BuildScripts(
        DesignCanvasViewModel canvas,
        GdsFactoryExportOptions options,
        MetalRoutingSpec? metalSpec,
        IEnumerable<ComponentTemplate> library,
        string mainScriptPath)
    {
        var templates = library.ToList();
        bool IsNazcaNative(Component c) =>
            InherentBackendClassifier.Classify(c, templates) == InherentBackend.Nazca;

        var nazcaScript = _nazcaExporter.ExportPartial(
            canvas, IsNazcaNative, NazcaPartialTopCellName, metalSpec);

        var partialGdsFileName = Path.GetFileNameWithoutExtension(
            PartialScriptPathFor(mainScriptPath)) + ".gds";
        var gdsFactoryScript = _gdsFactoryExporter.Export(
            canvas, options, metalSpec,
            include: c => !IsNazcaNative(c),
            mergeGdsFileName: partialGdsFileName);

        return new MixedBackendScriptSet(
            DesignerHeader(mainScriptPath, isMain: true) + gdsFactoryScript,
            DesignerHeader(mainScriptPath, isMain: false) + nazcaScript);
    }

    /// <summary>
    /// Two scripts, one GDS: the header spells out the run order inside each file so
    /// both stay editable and re-runnable outside Lunima — the same script-in-hand
    /// workflow the single-file nazca export offers.
    /// </summary>
    private static string DesignerHeader(string mainScriptPath, bool isMain)
    {
        var mainName = Path.GetFileName(mainScriptPath);
        var partialName = Path.GetFileName(PartialScriptPathFor(mainScriptPath));
        var partialGds = Path.GetFileNameWithoutExtension(partialName) + ".gds";
        var nl = Environment.NewLine;
        return isMain
            ? $"# Mixed-backend export — part 2 of 2 (gdsfactory).{nl}" +
              $"# Run AFTER '{partialName}' — this merges '{partialGds}' (must sit next to this script).{nl}"
            : $"# Mixed-backend export — part 1 of 2 (nazca).{nl}" +
              $"# Run FIRST — this writes '{partialGds}' next to itself; then run '{mainName}'.{nl}";
    }

    /// <summary>Flattens the canvas to exportable components (groups recursed, analysis
    /// tools skipped) — mirrors the enumeration both exporters use.</summary>
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
}
