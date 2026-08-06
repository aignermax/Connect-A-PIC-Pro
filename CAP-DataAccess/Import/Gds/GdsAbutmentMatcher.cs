namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Reconstructs abutment connections from absolute pin positions. Two pins of
/// DIFFERENT instances form a connection when their positions coincide within
/// the tolerance and their outward angles oppose (180° ± 5°); an instance pin
/// also connects to a top-cell port whose label position coincides (port
/// labels carry no reliable inward direction, so no angle check applies).
/// One partner per pin — first match in deterministic scan order wins, extra
/// candidates produce an ambiguity warning. No self-connections.
/// Pins already consumed by route-derived pairs
/// (<see cref="GdsRouteConnectivityMatcher"/>, which runs first) are excluded
/// up front, so a route polygon's connection is never double-connected.
///
/// Performance: partner candidates come from a <see cref="GdsSpatialGrid"/>
/// uniform hash over all pin positions instead of the quadratic all-pins scan.
/// The grid only prunes pins that geometrically cannot lie within the
/// tolerance and the ordinal sort restores the scan order, so the pairs, the
/// warnings, and their ordering are identical to the brute-force scan; what
/// was O(pins²) becomes near-linear (tens of thousands of pins on a
/// production-scale file).
/// </summary>
internal static class GdsAbutmentMatcher
{
    private const double OppositionToleranceDegrees = 5.0;

    /// <summary>A match candidate: an instance pin, or a top-cell port when <see cref="IsPort"/>.</summary>
    private readonly record struct Candidate(int InstanceIndex, int PinIndex, string PinName, bool IsPort);

    /// <summary>
    /// The pins in one flat deterministic scan order — all instance pins
    /// (instance placement order, then pin order), then the top-cell ports —
    /// with a spatial index over their positions. The ordinal IS the scan
    /// order: sorting grid candidates by it reproduces the brute-force nested
    /// loops exactly.
    /// </summary>
    private sealed class PinTable
    {
        public List<int> InstanceOf = new(); // −1 for a top-cell port
        public List<int> IndexOf = new();    // pin index within the instance, or port index
        public GdsSpatialGrid? Grid;

        public int Count => InstanceOf.Count;
    }

    /// <summary>
    /// Scans pins in deterministic order (instance placement order, then pin
    /// order; top-cell ports last) and returns the pairs in scan order.
    /// </summary>
    /// <param name="instanceNames">Instance display names, parallel to <paramref name="pinsPerInstance"/>.</param>
    /// <param name="pinsPerInstance">Absolute pins per instance index.</param>
    /// <param name="topPortPins">Absolute pins of the top cell's own port labels.</param>
    /// <param name="toleranceUm">Position coincidence tolerance in micrometers.</param>
    /// <param name="warnings">Collects user-presentable ambiguity warnings.</param>
    /// <param name="preConsumedInstancePins">
    /// Instance pins already paired by route derivation — treated as consumed
    /// from the start (never matched, never ambiguous).
    /// </param>
    /// <param name="preConsumedPortIndexes">
    /// Top-cell port indexes already paired by route derivation.
    /// </param>
    public static IReadOnlyList<GdsPinPair> Match(
        IReadOnlyList<string> instanceNames,
        IReadOnlyList<IReadOnlyList<GdsAbsolutePin>> pinsPerInstance,
        IReadOnlyList<GdsAbsolutePin> topPortPins,
        double toleranceUm,
        List<string> warnings,
        IReadOnlySet<(int InstanceIndex, int PinIndex)>? preConsumedInstancePins = null,
        IReadOnlySet<int>? preConsumedPortIndexes = null)
    {
        var pairs = new List<GdsPinPair>();
        var consumedInstancePins = pinsPerInstance.Select(pins => new bool[pins.Count]).ToArray();
        var consumedPorts = new bool[topPortPins.Count];
        if (preConsumedInstancePins is not null)
        {
            foreach (var (instance, pin) in preConsumedInstancePins)
                consumedInstancePins[instance][pin] = true;
        }
        if (preConsumedPortIndexes is not null)
        {
            foreach (var port in preConsumedPortIndexes)
                consumedPorts[port] = true;
        }

        var table = BuildPinTable(pinsPerInstance, topPortPins, toleranceUm);

        for (int i = 0; i < pinsPerInstance.Count; i++)
        {
            for (int k = 0; k < pinsPerInstance[i].Count; k++)
            {
                if (consumedInstancePins[i][k])
                    continue;

                var pin = pinsPerInstance[i][k];
                var candidates = CollectCandidates(pin, ownInstance: i);
                if (candidates.Count == 0)
                    continue;

                var chosen = candidates[0];
                if (candidates.Count > 1)
                {
                    warnings.Add(
                        $"Pin '{pin.Name}' of instance '{instanceNames[i]}' has {candidates.Count} " +
                        $"abutment candidates within {toleranceUm} µm; connected to " +
                        $"'{chosen.PinName}' ({Describe(chosen, instanceNames)}) — first match wins.");
                }

                consumedInstancePins[i][k] = true;
                var partnerPin = chosen.IsPort
                    ? topPortPins[chosen.PinIndex]
                    : pinsPerInstance[chosen.InstanceIndex][chosen.PinIndex];
                if (chosen.IsPort)
                    consumedPorts[chosen.PinIndex] = true;
                else
                    consumedInstancePins[chosen.InstanceIndex][chosen.PinIndex] = true;

                pairs.Add(new GdsPinPair
                {
                    A = new GdsPinEndpoint { InstanceIndex = i, PinName = pin.Name },
                    B = new GdsPinEndpoint { InstanceIndex = chosen.InstanceIndex, PinName = chosen.PinName },
                    XUm = (pin.XUm + partnerPin.XUm) / 2.0,
                    YUm = (pin.YUm + partnerPin.YUm) / 2.0,
                });
            }
        }
        return pairs;

        // The grid query box is a SUPERSET of the tolerance disk — it only
        // prunes pins that cannot possibly coincide; the original predicates
        // stay the arbiter, and the ordinal sort restores the brute-force scan
        // order (instance pins in placement order, then top-cell ports).
        List<Candidate> CollectCandidates(GdsAbsolutePin sourcePin, int ownInstance)
        {
            var candidates = new List<Candidate>();
            if (table.Grid is null)
                return candidates;
            var ordinals = table.Grid.QueryBox(
                sourcePin.XUm - toleranceUm, sourcePin.YUm - toleranceUm,
                sourcePin.XUm + toleranceUm, sourcePin.YUm + toleranceUm);
            ordinals.Sort();
            foreach (int ordinal in ordinals)
            {
                int j = table.InstanceOf[ordinal];
                int l = table.IndexOf[ordinal];
                if (j < 0)
                {
                    if (!consumedPorts[l] && WithinTolerance(sourcePin, topPortPins[l], toleranceUm))
                        candidates.Add(new Candidate(-1, l, topPortPins[l].Name, IsPort: true));
                }
                else if (j != ownInstance && !consumedInstancePins[j][l])
                {
                    var other = pinsPerInstance[j][l];
                    if (WithinTolerance(sourcePin, other, toleranceUm) && AnglesOppose(sourcePin, other))
                        candidates.Add(new Candidate(j, l, other.Name, IsPort: false));
                }
            }
            return candidates;
        }
    }

    /// <summary>
    /// Builds the flat pin table with its spatial index; the grid is null when
    /// there are no pins at all (the scan then collects no candidates). The
    /// cell size adapts to the pin spread and the coincidence tolerance.
    /// </summary>
    private static PinTable BuildPinTable(
        IReadOnlyList<IReadOnlyList<GdsAbsolutePin>> pinsPerInstance,
        IReadOnlyList<GdsAbsolutePin> topPortPins,
        double toleranceUm)
    {
        var table = new PinTable();
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        void Add(int instanceIndex, int pinIndex, GdsAbsolutePin pin)
        {
            table.InstanceOf.Add(instanceIndex);
            table.IndexOf.Add(pinIndex);
            minX = Math.Min(minX, pin.XUm);
            minY = Math.Min(minY, pin.YUm);
            maxX = Math.Max(maxX, pin.XUm);
            maxY = Math.Max(maxY, pin.YUm);
        }

        for (var i = 0; i < pinsPerInstance.Count; i++)
            for (var k = 0; k < pinsPerInstance[i].Count; k++)
                Add(i, k, pinsPerInstance[i][k]);
        for (var t = 0; t < topPortPins.Count; t++)
            Add(-1, t, topPortPins[t]);

        if (table.Count == 0)
            return table;

        var span = Math.Max(maxX - minX, maxY - minY);
        table.Grid = GdsSpatialGrid.Create(span, toleranceUm, table.Count);
        var ordinal = 0;
        for (var i = 0; i < pinsPerInstance.Count; i++)
            for (var k = 0; k < pinsPerInstance[i].Count; k++)
                table.Grid.InsertPoint(ordinal++, pinsPerInstance[i][k].XUm, pinsPerInstance[i][k].YUm);
        for (var t = 0; t < topPortPins.Count; t++)
            table.Grid.InsertPoint(ordinal++, topPortPins[t].XUm, topPortPins[t].YUm);
        return table;
    }

    private static bool WithinTolerance(GdsAbsolutePin a, GdsAbsolutePin b, double toleranceUm)
    {
        double dx = a.XUm - b.XUm;
        double dy = a.YUm - b.YUm;
        return dx * dx + dy * dy <= toleranceUm * toleranceUm;
    }

    /// <summary>True when the two outward angles point against each other (differ by 180° ± 5°).</summary>
    private static bool AnglesOppose(GdsAbsolutePin a, GdsAbsolutePin b) =>
        Math.Abs(GdsInstancePinProjector.Normalize180(a.AngleDegrees - b.AngleDegrees))
        >= 180.0 - OppositionToleranceDegrees;

    private static string Describe(Candidate candidate, IReadOnlyList<string> instanceNames) =>
        candidate.IsPort ? "top-cell port" : $"instance '{instanceNames[candidate.InstanceIndex]}'";
}
