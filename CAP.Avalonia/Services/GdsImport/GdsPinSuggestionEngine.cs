using CAP_DataAccess.Import.Gds;

namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// Preview engine for the GDS import dialog: scans the direct child cells of
/// the selected top cell, runs pin detection over the configured port and
/// waveguide layers, and returns every edge-heuristic pin as a user-reviewable
/// guess. Metal-only edges are excluded automatically because the detector
/// only scans the configured waveguide layers.
/// </summary>
/// <remarks>
/// This is a preview over the layers the user currently sees in the dialog. The
/// actual import additionally runs the any-layer label fallback and collapses
/// coincident label stacks in the full import session, so a cell
/// whose labels live on non-port layers may show heuristic guesses here that the
/// import later suppresses. Keeping the preview lightweight avoids recomputing
/// the full session on every keystroke; the fallback only changes which guesses
/// disappear, never which real (label) pins exist.
/// </remarks>
public static class GdsPinSuggestionEngine
{
    /// <summary>
    /// Builds the list of guessed pins for <paramref name="topCellName"/>.
    /// One <see cref="GdsPinSuggestion"/> is emitted for each
    /// <see cref="DetectedPinSource.EdgeHeuristic"/> pin found on a direct
    /// child cell. The list is deterministic: cells appear in the order of the
    /// first direct instance, and pins follow the detector's edge order.
    /// </summary>
    /// <param name="library">The parsed GDS library from analysis.</param>
    /// <param name="topCellName">The top cell the user selected.</param>
    /// <param name="options">The layer options currently configured in the dialog.</param>
    public static IReadOnlyList<GdsPinSuggestion> Build(
        GdsLibrary library, string topCellName, GdsHierarchyImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(topCellName);
        ArgumentNullException.ThrowIfNull(options);

        if (!library.Cells.TryGetValue(topCellName, out _))
            return Array.Empty<GdsPinSuggestion>();

        var flattener = new GdsCellFlattener(library);
        IReadOnlyList<GdsInstance>? instances;
        try
        {
            instances = flattener.GetInstanceTree(topCellName);
        }
        catch (InvalidDataException)
        {
            // A broken hierarchy should fail during import, not during preview.
            return Array.Empty<GdsPinSuggestion>();
        }

        var suggestions = new List<GdsPinSuggestion>();
        var seenCells = new HashSet<string>(StringComparer.Ordinal);
        var discardedWarnings = new List<string>();

        foreach (var instance in instances)
        {
            if (!seenCells.Add(instance.CellName))
                continue;

            if (!library.Cells.TryGetValue(instance.CellName, out _))
                continue;

            suggestions.AddRange(CollectGuessedPins(
                library, instance.CellName, flattener, options.PinDetection, discardedWarnings));
        }

        return suggestions;
    }

    private static IEnumerable<GdsPinSuggestion> CollectGuessedPins(
        GdsLibrary library,
        string cellName,
        GdsCellFlattener flattener,
        GdsPinDetectionOptions pinDetection,
        List<string> warnings)
    {
        GdsBoundingBox bbox;
        FlattenedGdsCell flattened;
        try
        {
            bbox = flattener.GetBoundingBox(cellName);
            if (bbox.Width <= 0 || bbox.Height <= 0)
                yield break;

            flattened = flattener.Flatten(cellName);
        }
        catch (InvalidDataException)
        {
            yield break;
        }

        var detectionCell = new FlattenedGdsCell { CellName = cellName };
        detectionCell.Polygons.AddRange(flattened.Polygons);

        // Use the cell's own texts, not flattened nested labels: explode-mode
        // drafts are built from the cell's own port labels.
        foreach (var text in library.Cells[cellName].Elements.OfType<GdsText>())
        {
            if (!text.Text.Contains('\n'))
                detectionCell.Texts.Add(text);
        }

        var pins = GdsPinDetector.Detect(detectionCell, bbox, pinDetection);
        var normalized = GdsPinNameNormalizer.Normalize(pins, $"Cell '{cellName}'", warnings);

        foreach (var pin in normalized.Where(p => p.Source == DetectedPinSource.EdgeHeuristic))
            yield return new GdsPinSuggestion(cellName, pin);
    }
}
