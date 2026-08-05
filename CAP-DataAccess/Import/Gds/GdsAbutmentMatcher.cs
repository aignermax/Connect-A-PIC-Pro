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
/// </summary>
internal static class GdsAbutmentMatcher
{
    private const double OppositionToleranceDegrees = 5.0;

    /// <summary>A match candidate: an instance pin, or a top-cell port when <see cref="IsPort"/>.</summary>
    private readonly record struct Candidate(int InstanceIndex, int PinIndex, string PinName, bool IsPort);

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

        for (int i = 0; i < pinsPerInstance.Count; i++)
        {
            for (int k = 0; k < pinsPerInstance[i].Count; k++)
            {
                if (consumedInstancePins[i][k])
                    continue;

                var pin = pinsPerInstance[i][k];
                var candidates = new List<Candidate>();

                for (int j = 0; j < pinsPerInstance.Count; j++)
                {
                    if (j == i)
                        continue;
                    for (int l = 0; l < pinsPerInstance[j].Count; l++)
                    {
                        if (consumedInstancePins[j][l])
                            continue;
                        var other = pinsPerInstance[j][l];
                        if (WithinTolerance(pin, other, toleranceUm) && AnglesOppose(pin, other))
                            candidates.Add(new Candidate(j, l, other.Name, IsPort: false));
                    }
                }
                for (int p = 0; p < topPortPins.Count; p++)
                {
                    if (consumedPorts[p])
                        continue;
                    if (WithinTolerance(pin, topPortPins[p], toleranceUm))
                        candidates.Add(new Candidate(-1, p, topPortPins[p].Name, IsPort: true));
                }

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
