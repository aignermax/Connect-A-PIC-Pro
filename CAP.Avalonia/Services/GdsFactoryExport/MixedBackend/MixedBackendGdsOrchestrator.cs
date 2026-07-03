using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Export;
using CAP_DataAccess.Persistence.PIR;

namespace CAP.Avalonia.Services.GdsFactoryExport.MixedBackend;

/// <summary>
/// Composes one GDS from a design that mixes Nazca-backend and gdsfactory-backend
/// instances (issue #646). Two-phase flow: (1) the Nazca emitter renders the
/// Nazca-backend override instances into a part GDS; (2) the gdsfactory host script
/// imports that GDS at the origin and adds everything else (PDK cells, stubs,
/// gdsfactory overrides, waveguide routes — route geometry is backend-neutral, so it
/// is always emitted in the host). Both emitters place at the same absolute
/// <see cref="NazcaCoordinateMapper"/> coordinates, so alignment carries over.
/// </summary>
public class MixedBackendGdsOrchestrator
{
    /// <summary>File-name suffix (before the extension) of the Nazca part script/GDS.</summary>
    public const string NazcaPartSuffix = "_nazca_part";

    private readonly GdsExportService _exportService;
    private readonly GdsFactoryExporter _gdsFactoryExporter = new();
    private readonly NazcaPartialExporter _nazcaPartialExporter = new();

    /// <summary>Initializes the orchestrator with the script runner used for both phases.</summary>
    /// <param name="exportService">Runs the generated Python scripts to produce GDS files.</param>
    public MixedBackendGdsOrchestrator(GdsExportService exportService)
    {
        _exportService = exportService;
    }

    /// <summary>
    /// True when the design contains at least one Nazca-backend raw-code override,
    /// i.e. the gdsfactory export needs the mixed two-phase flow.
    /// </summary>
    public static bool RequiresMixedExport(
        DesignCanvasViewModel canvas, IReadOnlyDictionary<string, NazcaCodeOverride>? overrides) =>
        NazcaPartialExporter.CollectNazcaBackendOverrideIds(canvas, overrides).Count > 0;

    /// <summary>
    /// Runs the mixed export: writes and runs the Nazca part script, then writes and runs
    /// the gdsfactory host script that merges the part GDS. Returns the host run's result
    /// (its <c>GdsPath</c> is the final composed GDS), or the Nazca phase's failure.
    /// </summary>
    /// <param name="canvas">The design canvas to export.</param>
    /// <param name="options">Component representation mode for the gdsfactory host.</param>
    /// <param name="overrides">Per-instance overrides keyed by component identifier.</param>
    /// <param name="hostScriptPath">Path of the gdsfactory host script (the file the user chose).</param>
    /// <param name="progress">Optional phase status updates for the UI.</param>
    public async Task<GdsExportService.ExportResult> ExportMixedAsync(
        DesignCanvasViewModel canvas,
        GdsFactoryExportOptions options,
        IReadOnlyDictionary<string, NazcaCodeOverride> overrides,
        string hostScriptPath,
        IProgress<string>? progress = null)
    {
        var partScriptPath = GetNazcaPartScriptPath(hostScriptPath);

        progress?.Report("Rendering Nazca-backend overrides with Nazca...");
        await File.WriteAllTextAsync(partScriptPath, _nazcaPartialExporter.Export(canvas, overrides));
        var partResult = await _exportService.ExportToGdsAsync(partScriptPath, generateGds: true);
        if (!partResult.Success || partResult.GdsPath == null)
        {
            return new GdsExportService.ExportResult
            {
                ScriptPath = hostScriptPath,
                Success = false,
                Status = "Nazca part generation failed",
                ErrorMessage = "Rendering the Nazca-backend overrides failed: "
                               + (partResult.ErrorMessage ?? partResult.Status),
            };
        }

        progress?.Report("Composing the final GDS with gdsfactory...");
        var merge = new NazcaGdsMerge(
            NazcaPartialExporter.CollectNazcaBackendOverrideIds(canvas, overrides)
                .ToHashSet(StringComparer.Ordinal),
            Path.GetFileName(partResult.GdsPath));
        await File.WriteAllTextAsync(hostScriptPath,
            _gdsFactoryExporter.Export(canvas, options, overrides, merge));
        return await _exportService.ExportToGdsAsync(hostScriptPath, generateGds: true);
    }

    /// <summary>Derives the Nazca part script path from the host script path.</summary>
    public static string GetNazcaPartScriptPath(string hostScriptPath)
    {
        var directory = Path.GetDirectoryName(hostScriptPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(hostScriptPath);
        return Path.Combine(directory, stem + NazcaPartSuffix + ".py");
    }
}
