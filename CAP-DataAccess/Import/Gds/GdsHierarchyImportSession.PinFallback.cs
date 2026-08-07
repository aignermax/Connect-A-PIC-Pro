using System.Text.RegularExpressions;

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
    /// nazca's bounding-box anchor labels (top-left, bottom-center, …): placement
    /// anchors every nazca cell carries, never ports. Only the guessing fallback
    /// filters them — a user who configures a port layer explicitly keeps every
    /// label on it, unlucky names included.
    /// </summary>
    private static readonly HashSet<string> NazcaAnchorNames = new(StringComparer.Ordinal)
    {
        "tl", "tc", "tr", "lt", "ct", "rt",
        "lc", "cc", "rc",
        "lb", "cb", "rb", "bl", "bc", "br",
        "cl", "cr",
    };

    /// <summary>
    /// Parameter annotations like <c>R:0.0001</c> or <c>n=1.0</c>: a name/value
    /// pair with a numeric tail is cell metadata, not a port.
    /// </summary>
    private static readonly Regex ParameterLabelPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_]*\s*[:=]\s*[-+]?[0-9][0-9.eE+-]*$", RegexOptions.Compiled);

    /// <summary>True for labels the auto-discovery must never turn into pins.</summary>
    private static bool IsFallbackGhostLabel(GdsText text)
    {
        var name = text.Text.Trim();
        return NazcaAnchorNames.Contains(name) || ParameterLabelPattern.IsMatch(name);
    }

    /// <summary>
    /// True once any cell's pins came from the any-layer fallback — the signal
    /// that this file does not follow our own export conventions, so layer-number
    /// defaults (e.g. the metal layers) may not apply either.
    /// </summary>
    internal bool LabelFallbackUsed { get; private set; }

    /// <summary>
    /// Runs <see cref="GdsPinDetector"/> with the configured port layers. When
    /// that yields ZERO label pins although the cell carries text labels on
    /// OTHER layers, retries once with every text label treated as a pin label:
    /// real foundry files place pin labels on their own layers (e.g. a big
    /// production design places device pins on (56,0)/(59,0)/(233,0)/(235,0)),
    /// so a configured-only pass finds nothing and the whole import would
    /// silently drop the cell. Configured layers always win; the fallback never
    /// mixes (a single configured label pin skips it). Anchor/parameter helper
    /// labels (<see cref="IsFallbackGhostLabel"/>) are excluded from the retry —
    /// they otherwise become ghost pins on every nazca-produced cell. A used
    /// fallback emits ONE info note per cell listing the layer(s) pins were
    /// found on, so the user can add them to the port-layer list.
    /// Deterministic: layers reported sorted.
    /// </summary>
    private IReadOnlyList<DetectedPin> DetectWithAnyLayerFallback(
        FlattenedGdsCell detectionCell, GdsBoundingBox bbox, string cellName)
    {
        var pins = GdsPinDetector.Detect(detectionCell, bbox, _options.PinDetection);
        if (pins.Any(p => p.Source == DetectedPinSource.Label))
            return pins;

        var fallbackCell = BuildGhostFilteredCopy(detectionCell, cellName);
        var fallbackLayers = fallbackCell.Texts
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
        var fallbackPins = GdsPinDetector.Detect(fallbackCell, bbox, _options.PinDetection with
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

    /// <summary>
    /// The detection cell without anchor/parameter helper labels, with
    /// <see cref="FlattenedGdsCell.TextOrigins"/> kept index-aligned. Emits one
    /// info note naming what was ignored (real ports with unlucky names stay
    /// diagnosable). Returns the original instance when nothing was filtered.
    /// </summary>
    private FlattenedGdsCell BuildGhostFilteredCopy(FlattenedGdsCell detectionCell, string cellName)
    {
        var ghosts = detectionCell.Texts.Where(IsFallbackGhostLabel).ToList();
        if (ghosts.Count == 0)
            return detectionCell;

        var filtered = new FlattenedGdsCell { CellName = detectionCell.CellName };
        filtered.Polygons.AddRange(detectionCell.Polygons);
        bool hasOrigins = detectionCell.TextOrigins.Count == detectionCell.Texts.Count;
        for (int i = 0; i < detectionCell.Texts.Count; i++)
        {
            if (IsFallbackGhostLabel(detectionCell.Texts[i]))
                continue;
            filtered.Texts.Add(detectionCell.Texts[i]);
            if (hasOrigins)
                filtered.TextOrigins.Add(detectionCell.TextOrigins[i]);
        }

        var ignored = string.Join(", ", ghosts.Select(g => $"'{g.Text.Trim()}'").Distinct().Take(8));
        Infos.Add(
            $"Cell '{cellName}': {ghosts.Count} helper label(s) ignored by pin auto-discovery " +
            $"(bounding-box anchors / parameter annotations: {ignored}).");
        return filtered;
    }
}
