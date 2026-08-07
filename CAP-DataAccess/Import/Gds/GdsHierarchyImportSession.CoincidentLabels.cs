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
/// non-configured label; the pin name comes from the winner's text. Several
/// configured labels in one stack tie-break by FILE ORDER — the first
/// configured label wins.</item>
/// <item>With no configured-layer label in the stack the FIRST label in file
/// order wins — it stands for the label set the any-layer fallback
/// (<see cref="DetectWithAnyLayerFallback"/>) would discover anyway.</item>
/// <item>Labels tied on the winner's OWN (layer, texttype) with DIFFERENT
/// texts are ALL kept: real duplicate pins must never silently merge (the
/// duplicate-name handling in <c>GdsPinNameNormalizer</c> deals out unique
/// names). Those kept duplicates at one anchor still inflate the pin count of
/// any route network touching it — accepted, because merging away real pins
/// is worse than a frozen path. Byte-IDENTICAL copies (same text, layer and
/// anchor — e.g. an exporter writing one label twice) are not real duplicates:
/// only the FIRST copy is kept, silently, exactly like the black-box
/// fingerprint dedup (<see cref="GetBlackBoxPins"/>). Kept, the copies would
/// read as several pins at one position — re-inflating the touching network
/// into a junction — and the duplicate-name rename would fire a warning for
/// what is physically one pin.</item>
/// </list>
///
/// The dedup runs on the TEXTS, before <see cref="GdsPinDetector"/> sees them,
/// so the edge heuristic's label-coverage suppression also treats the stack as
/// one pin (no phantom pins either way), and the fallback's discovered-layer
/// list is no longer polluted by dropped label layers. Every stack that
/// dropped a DIFFERENT label is reported — ONE aggregated info note per cell,
/// never one per pin; verbatim-doubled labels collapse silently.
/// </summary>
internal sealed partial class GdsHierarchyImportSession
{
    /// <summary>One collapsed label stack, for the aggregated per-cell info note.</summary>
    private readonly record struct CoincidentLabelMerge(string KeptName, int Layer, int TextType, int DroppedCount);

    /// <summary>
    /// Collapses coincident label stacks in <paramref name="texts"/> (see the
    /// class-level remarks) and returns the surviving labels in their original
    /// file order. Emits the aggregated info note into <see cref="Infos"/>
    /// when at least one stack dropped a different label.
    /// </summary>
    private List<GdsText> CollapseCoincidentLabels(IReadOnlyList<GdsText> texts, string cellName)
    {
        if (texts.Count < 2)
            return [.. texts];

        var keep = new bool[texts.Count];
        var merges = new List<CoincidentLabelMerge>();
        foreach (var stack in GroupIntoStacks(texts, _options.PinDetection.EdgeTouchToleranceUm))
        {
            // The stack's winner: the first CONFIGURED label (file order is the
            // deterministic tie-break between several configured labels), else
            // the first label in file order.
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
            var keptTexts = new HashSet<string>(StringComparer.Ordinal);
            int dropped = 0;
            foreach (int i in stack)
            {
                if (texts[i].Layer == winner.Layer && texts[i].TextType == winner.TextType)
                {
                    // Byte-identical copies (same text on the winner's own layer
                    // and anchor) describe the same physical pin — keep only the
                    // first, silently (see the class remarks).
                    if (keptTexts.Add(texts[i].Text))
                        keep[i] = true;
                }
                else
                {
                    dropped++;
                }
            }
            if (dropped > 0)
                merges.Add(new CoincidentLabelMerge(winner.Text, winner.Layer, winner.TextType, dropped));
        }

        if (merges.Count > 0)
        {
            var details = string.Join("; ", merges.Select(m =>
                $"'{m.KeptName}' kept from layer ({m.Layer},{m.TextType}), " +
                $"{m.DroppedCount} {(m.DroppedCount == 1 ? "label" : "labels")} ignored"));
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

    /// <summary>
    /// Groups the label indexes into coincident stacks: greedy grouping on each
    /// stack's FIRST anchor (stacked labels share their anchor within a database
    /// unit), first-seen order deciding which stack a label joins. The anchors
    /// are bucketed through a <see cref="GdsSpatialGrid"/> so a label only
    /// compares against the stacks near it — comparing every label to every
    /// stack is O(n²), and production cells carry hundreds of labels. The
    /// grid's candidates come back unordered; the ascending sort restores the
    /// naive scan's lowest-stack-index win, so the grouping is exactly the naive
    /// scan's, deterministic in file order.
    /// </summary>
    private static List<List<int>> GroupIntoStacks(IReadOnlyList<GdsText> texts, double tolerance)
    {
        double toleranceSquared = tolerance * tolerance;
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var text in texts)
        {
            minX = Math.Min(minX, text.Position.X);
            minY = Math.Min(minY, text.Position.Y);
            maxX = Math.Max(maxX, text.Position.X);
            maxY = Math.Max(maxY, text.Position.Y);
        }

        var grid = GdsSpatialGrid.Create(Math.Max(maxX - minX, maxY - minY), tolerance, texts.Count);
        var stackAnchors = new List<GdsPoint>(texts.Count);
        var stacks = new List<List<int>>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            var position = texts[i].Position;
            var candidates = grid.QueryBox(
                position.X - tolerance, position.Y - tolerance,
                position.X + tolerance, position.Y + tolerance);
            candidates.Sort();
            int stackIndex = -1;
            foreach (int s in candidates)
            {
                double dx = stackAnchors[s].X - position.X;
                double dy = stackAnchors[s].Y - position.Y;
                if (dx * dx + dy * dy <= toleranceSquared)
                {
                    stackIndex = s;
                    break;
                }
            }
            if (stackIndex < 0)
            {
                stackIndex = stacks.Count;
                stackAnchors.Add(position);
                stacks.Add(new List<int>());
                grid.InsertPoint(stackIndex, position.X, position.Y);
            }
            stacks[stackIndex].Add(i);
        }
        return stacks;
    }
}
