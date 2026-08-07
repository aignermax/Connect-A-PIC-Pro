namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// The label-layer auto-discovery fallback of <see cref="GdsHierarchyImportSession"/>,
/// split out to keep the session file under the project's 500-line gate.
/// </summary>
internal sealed partial class GdsHierarchyImportSession
{
    /// <summary>
    /// Runs <see cref="GdsPinDetector"/> with the configured port layers. When
    /// that yields ZERO label pins although the cell carries text labels on
    /// OTHER layers, retries once with every text label treated as a pin label:
    /// real foundry files place pin labels on their own layers (e.g. a big
    /// production design places device pins on (56,0)/(59,0)/(233,0)/(235,0)),
    /// so a configured-only pass finds nothing and the whole import would
    /// silently drop the cell. Configured layers always win; the fallback never
    /// mixes (a single configured label pin skips it). A used fallback emits ONE
    /// info note per cell listing the layer(s) pins were found on, so the user
    /// can add them to the port-layer list. Deterministic: layers reported sorted.
    /// </summary>
    private IReadOnlyList<DetectedPin> DetectWithAnyLayerFallback(
        FlattenedGdsCell detectionCell, GdsBoundingBox bbox, string cellName)
    {
        var pins = GdsPinDetector.Detect(detectionCell, bbox, _options.PinDetection);
        if (pins.Any(p => p.Source == DetectedPinSource.Label))
            return pins;

        var fallbackLayers = detectionCell.Texts
            .Select(t => (t.Layer, t.TextType))
            .Where(pair => !_options.PinDetection.PortLayers.Contains(pair))
            .Distinct()
            .OrderBy(pair => pair.Layer)
            .ThenBy(pair => pair.TextType)
            .ToList();
        if (fallbackLayers.Count == 0)
            return pins;

        // Re-run with the discovered layers unioned in: label pins on those
        // layers plus the (unchanged) edge heuristic — label anchors now also
        // suppress the heuristic touches they cover.
        var fallbackPins = GdsPinDetector.Detect(detectionCell, bbox, _options.PinDetection with
        {
            PortLayers = [.. _options.PinDetection.PortLayers, .. fallbackLayers],
        });
        if (!fallbackPins.Any(p => p.Source == DetectedPinSource.Label))
            return pins;

        var layerList = string.Join(", ", fallbackLayers.Select(l => $"({l.Layer},{l.TextType})"));
        Infos.Add(fallbackLayers.Count == 1
            ? $"Cell '{cellName}': pins detected on non-standard layer {layerList} — " +
              "add it to the port-layer list to silence this note."
            : $"Cell '{cellName}': pins detected on non-standard layers {layerList} — " +
              "add them to the port-layer list to silence this note.");
        return fallbackPins;
    }
}
