namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// The label-layer auto-discovery fallback of <see cref="GdsHierarchyImportSession"/>,
/// split out to keep the session file under the project's 500-line gate.
/// </summary>
internal sealed partial class GdsHierarchyImportSession
{
    /// <summary>
    /// Multi-line texts are metadata blobs (e.g. nazca's "cellname: …\nfoundry_pdk: …"),
    /// never port labels — a pin name cannot span lines. Applied everywhere texts
    /// become pin candidates: top-level ports, draft pins and black-box pins.
    /// </summary>
    private static bool IsSingleLineLabel(GdsText text) => !text.Text.Contains('\n');

    /// <summary>
    /// True once any cell's pins came from the any-layer fallback — the signal
    /// that this file does not follow our own export conventions, so layer-number
    /// defaults (e.g. the metal layers) may not apply either.
    /// </summary>
    internal bool LabelFallbackUsed { get; private set; }

    /// <summary>
    /// Runs <see cref="GdsPinDetector"/> with the configured port layers. When
    /// that yields ZERO label pins although the cell carries port-like text
    /// labels on OTHER layers, retries once with every remaining text label
    /// treated as a pin label: real foundry files place pin labels on their own
    /// layers (e.g. a big production design places device pins on (56,0)/(59,0)/(233,0)/(235,0)),
    /// so a configured-only pass finds nothing and the whole import would
    /// silently drop the cell. Configured layers always win; the fallback never
    /// mixes (a single configured label pin skips it). Anchor/parameter helper
    /// labels (<see cref="GdsGhostLabelFilter"/>) never become pins on either
    /// path and never qualify a layer for the retry — a cell carrying ONLY such
    /// labels triggers no fallback at all. A used fallback emits ONE info note
    /// per cell listing the layer(s) pins were found on, so the user can add
    /// them to the port-layer list. Deterministic: layers reported sorted.
    /// </summary>
    private IReadOnlyList<DetectedPin> DetectWithAnyLayerFallback(
        FlattenedGdsCell detectionCell, GdsBoundingBox bbox, string cellName)
    {
        var pins = GdsPinDetector.Detect(detectionCell, bbox, _options.PinDetection);
        if (pins.Any(p => p.Source == DetectedPinSource.Label))
            return pins;

        var ghosts = detectionCell.Texts.Where(GdsGhostLabelFilter.IsGhost).ToList();
        if (ghosts.Count > 0)
        {
            var ignored = string.Join(", ", ghosts.Select(g => $"'{g.Text.Trim()}'").Distinct().Take(8));
            Infos.Add(
                $"Cell '{cellName}': {ghosts.Count} helper label(s) ignored by pin auto-discovery " +
                $"(bounding-box anchors / parameter annotations: {ignored}).");
        }

        var fallbackLayers = detectionCell.Texts
            .Where(t => !GdsGhostLabelFilter.IsGhost(t))
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

        LabelFallbackUsed = true;
        var layerList = string.Join(", ", fallbackLayers.Select(l => $"({l.Layer},{l.TextType})"));
        Infos.Add(fallbackLayers.Count == 1
            ? $"Cell '{cellName}': pins detected on non-standard layer {layerList} — " +
              "add it to the port-layer list to silence this note."
            : $"Cell '{cellName}': pins detected on non-standard layers {layerList} — " +
              "add them to the port-layer list to silence this note.");
        return fallbackPins;
    }
}
