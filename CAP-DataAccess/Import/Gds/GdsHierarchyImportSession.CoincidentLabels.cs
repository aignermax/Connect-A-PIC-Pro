namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// The coincident-label dedup of <see cref="GdsHierarchyImportSession"/>,
/// split out to keep the session file under the project's 500-line gate.
///
/// Real foundry layouts stack several TEXT labels on the SAME pin anchor: the
/// real pin label (e.g. <c>o1</c> on the foundry's pin layer) plus helper /
/// marker labels (e.g. <c>lc</c>) on other layers. Taken literally every label
/// becomes its own pin, and the identical positions then inflate every route
/// network's pin count — a true 2-pin connection reads as a &gt;2-pin junction
/// and stays frozen instead of becoming a connection.
///
/// Labels whose anchors coincide within
/// <see cref="GdsPinDetectionOptions.EdgeTouchToleranceUm"/> (stacked labels
/// share their database-unit anchor) therefore collapse into ONE label before
/// pin detection runs:
/// <list type="bullet">
/// <item>A label on a CONFIGURED port layer
/// (<see cref="GdsPinDetectionOptions.PortLayers"/>) wins over every
/// non-configured helper label; the pin name comes from the winner's
/// text.</item>
/// <item>With no configured-layer label in the stack the FIRST label in file
/// order wins — it stands for the label set the any-layer fallback
/// (<see cref="DetectWithAnyLayerFallback"/>) would discover anyway.</item>
/// <item>Labels tied on the winner's OWN (layer, texttype) are ALL kept: real
/// duplicate pins must never silently merge (the duplicate-name handling in
/// <c>GdsPinNameNormalizer</c> deals out unique names).</item>
/// </list>
///
/// The dedup runs on the TEXTS, before <see cref="GdsPinDetector"/> sees them,
/// so the edge heuristic's label-coverage suppression also treats the stack as
/// one pin (no phantom pins either way), and the fallback's discovered-layer
/// list is no longer polluted by dropped helper layers. Every stack that
/// actually dropped a label is reported — ONE aggregated info note per cell,
/// never one per pin.
/// </summary>
internal sealed partial class GdsHierarchyImportSession
{
    /// <summary>One collapsed label stack, for the aggregated per-cell info note.</summary>
    private readonly record struct CoincidentLabelMerge(string KeptName, int Layer, int TextType, int DroppedCount);

    /// <summary>
    /// Collapses coincident label stacks in <paramref name="texts"/> (see the
    /// class-level remarks) and returns the surviving labels in their original
    /// file order. Emits the aggregated info note into <see cref="Infos"/>
    /// when at least one stack dropped a label.
    /// </summary>
    private List<GdsText> CollapseCoincidentLabels(IReadOnlyList<GdsText> texts, string cellName)
    {
        if (texts.Count < 2)
            return [.. texts];

        double tolerance = _options.PinDetection.EdgeTouchToleranceUm;
        double toleranceSquared = tolerance * tolerance;

        // Greedy grouping on each stack's FIRST anchor: stacked labels share
        // their anchor within a database unit, and first-seen order is the
        // deterministic tie-break for the winner.
        var stackAnchors = new List<GdsPoint>(texts.Count);
        var stacks = new List<List<int>>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            int stackIndex = -1;
            for (var s = 0; s < stackAnchors.Count; s++)
            {
                double dx = stackAnchors[s].X - texts[i].Position.X;
                double dy = stackAnchors[s].Y - texts[i].Position.Y;
                if (dx * dx + dy * dy <= toleranceSquared)
                {
                    stackIndex = s;
                    break;
                }
            }
            if (stackIndex < 0)
            {
                stackIndex = stacks.Count;
                stackAnchors.Add(texts[i].Position);
                stacks.Add(new List<int>());
            }
            stacks[stackIndex].Add(i);
        }

        var keep = new bool[texts.Count];
        var merges = new List<CoincidentLabelMerge>();
        foreach (var stack in stacks)
        {
            int winnerIndex = stack[0];
            foreach (int i in stack)
            {
                if (_options.PinDetection.PortLayers.Contains((texts[i].Layer, texts[i].TextType)))
                {
                    winnerIndex = i;
                    break;
                }
            }

            var winner = texts[winnerIndex];
            int kept = 0;
            foreach (int i in stack)
            {
                if (texts[i].Layer == winner.Layer && texts[i].TextType == winner.TextType)
                {
                    keep[i] = true;
                    kept++;
                }
            }
            if (kept < stack.Count)
                merges.Add(new CoincidentLabelMerge(winner.Text, winner.Layer, winner.TextType, stack.Count - kept));
        }

        if (merges.Count > 0)
        {
            var details = string.Join("; ", merges.Select(m =>
                $"'{m.KeptName}' kept from layer ({m.Layer},{m.TextType}), " +
                $"{m.DroppedCount} helper {(m.DroppedCount == 1 ? "label" : "labels")} ignored"));
            Infos.Add(merges.Count == 1
                ? $"Cell '{cellName}': coincident labels merged into one pin ({details})."
                : $"Cell '{cellName}': coincident labels merged into one pin at {merges.Count} stacked anchors ({details}).");
        }

        var survivors = new List<GdsText>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            if (keep[i])
                survivors.Add(texts[i]);
        }
        return survivors;
    }
}
