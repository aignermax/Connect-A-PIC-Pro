using CAP.Avalonia.Services.Localization;
using CAP_DataAccess.Import.Gds;

namespace CAP.Avalonia.ViewModels.GdsImport;

/// <summary>
/// The option-parsing half of <see cref="GdsImportDialogViewModel"/> (layer
/// fields), split out to keep the dialog ViewModel under the project's
/// 500-line gate.
/// </summary>
public partial class GdsImportDialogViewModel
{
    /// <summary>
    /// Builds the import options from the mode radio and the layer text fields.
    /// Internal as a test seam (InternalsVisibleTo UnitTests).
    /// </summary>
    internal bool TryBuildOptions(out GdsHierarchyImportOptions options, out string? error)
    {
        options = new GdsHierarchyImportOptions();
        error = null;

        var portLayers = ParseLayerPairs(PortLayersText);
        if (portLayers is null)
        {
            error = string.Format(
                LocalizationService.Instance.Translate("GdsImport.ErrorLayerSyntax"), PortLayersText);
            return false;
        }
        var waveguideLayers = ParseLayerPairs(WaveguideLayersText);
        if (waveguideLayers is null)
        {
            error = string.Format(
                LocalizationService.Instance.Translate("GdsImport.ErrorLayerSyntax"), WaveguideLayersText);
            return false;
        }

        // Empty = AUTO (null): the importer resolves the metal layers via the
        // Lunima export sentinel — own exports get the exporter defaults,
        // foreign files get none. A non-empty value is the user's explicit
        // mapping and feeds both route matching and electrical pin inference.
        List<(int Layer, int Datatype)>? metalLayers = null;
        if (!string.IsNullOrWhiteSpace(MetalLayersText))
        {
            metalLayers = ParseLayerPairs(MetalLayersText);
            if (metalLayers is null)
            {
                error = string.Format(
                    LocalizationService.Instance.Translate("GdsImport.ErrorLayerSyntax"), MetalLayersText);
                return false;
            }
        }

        options = options with
        {
            Mode = IsExplodeMode ? GdsHierarchyImportMode.ExplodeHierarchy : GdsHierarchyImportMode.BlackBox,
            MetalRouteLayers = metalLayers,
            PinDetection = new GdsPinDetectionOptions
            {
                PortLayers = portLayers,
                WaveguideLayers = waveguideLayers,
                ElectricalLayers = metalLayers,
            },
        };
        return true;
    }

    /// <summary>
    /// Parses "layer,datatype" pairs separated by ';' (e.g. <c>1,10</c> or
    /// <c>1,10; 2,0</c>). Returns null when any segment is malformed — GDS
    /// layer/datatype numbers are unsigned, so negative values are rejected too.
    /// </summary>
    internal static List<(int Layer, int Datatype)>? ParseLayerPairs(string text)
    {
        var pairs = new List<(int, int)>();
        foreach (var segment in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = segment.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || !int.TryParse(parts[0], out var layer)
                || !int.TryParse(parts[1], out var datatype)
                || layer < 0
                || datatype < 0)
            {
                return null;
            }
            pairs.Add((layer, datatype));
        }
        return pairs.Count > 0 ? pairs : null;
    }
}
