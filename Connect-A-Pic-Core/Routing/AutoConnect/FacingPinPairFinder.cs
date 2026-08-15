using CAP_Core.Components.Core;

namespace CAP_Core.Routing.AutoConnect;

/// <summary>
/// Pairs up unconnected pins that face each other so an auto-connect pass can
/// route each pair (GDS import rung 1, issue #880). Two pins pair when they
/// share a signal domain (optical/electrical), their outward angles oppose
/// within a tolerance, and each pin lies strictly in FRONT of the other —
/// back-to-back or sideways pins never pair, no matter how close. Among all
/// valid combinations, pairs are assigned greedily nearest-first, so each pin
/// gets its closest still-free facing partner.
/// </summary>
public sealed class FacingPinPairFinder
{
    /// <summary>
    /// Maximum deviation from perfect 180° opposition for two pins to count as
    /// facing. Deliberately looser than the abutment matcher's 5°: auto-connect
    /// bridges gaps with routed bends, not exact butt joints.
    /// </summary>
    public const double OppositionToleranceDegrees = 45.0;

    /// <summary>
    /// Minimum forward projection (µm) of the partner onto a pin's outward
    /// direction. Coincident pins project 0 and stay unpaired — a perfect
    /// abutment is the import matcher's job, not the router's.
    /// </summary>
    public const double MinForwardProjectionUm = 0.001;

    /// <summary>
    /// Finds mutually facing pairs among <paramref name="candidates"/>,
    /// nearest-first; candidates without a facing partner are returned unpaired.
    /// </summary>
    public FacingPinPairResult FindPairs(IReadOnlyList<FacingPinCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var scoredPairs = CollectFacingCombinations(candidates);
        scoredPairs.Sort((p, q) => p.Distance.CompareTo(q.Distance));

        var paired = new bool[candidates.Count];
        var pairs = new List<FacingPinPair>();
        foreach (var (a, b, distance) in scoredPairs)
        {
            if (paired[a] || paired[b])
                continue;
            paired[a] = true;
            paired[b] = true;
            pairs.Add(new FacingPinPair(candidates[a].Pin, candidates[b].Pin, distance));
        }

        var unpaired = new List<PhysicalPin>();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!paired[i])
                unpaired.Add(candidates[i].Pin);
        }
        return new FacingPinPairResult(pairs, unpaired);
    }

    private static List<(int A, int B, double Distance)> CollectFacingCombinations(
        IReadOnlyList<FacingPinCandidate> candidates)
    {
        var result = new List<(int, int, double)>();
        for (var i = 0; i < candidates.Count; i++)
        {
            for (var j = i + 1; j < candidates.Count; j++)
            {
                if (IsFacingPair(candidates[i], candidates[j], out var distance))
                    result.Add((i, j, distance));
            }
        }
        return result;
    }

    private static bool IsFacingPair(FacingPinCandidate a, FacingPinCandidate b, out double distance)
    {
        distance = 0;
        if (a.IsElectrical != b.IsElectrical)
            return false;
        if (!AnglesOppose(a.AngleDegrees, b.AngleDegrees))
            return false;

        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        if (!IsInFront(a.AngleDegrees, dx, dy) || !IsInFront(b.AngleDegrees, -dx, -dy))
            return false;

        distance = Math.Sqrt(dx * dx + dy * dy);
        return true;
    }

    private static bool AnglesOppose(double angleA, double angleB)
    {
        var diff = Math.Abs(angleA - angleB) % 360.0;
        if (diff > 180.0)
            diff = 360.0 - diff;
        return diff >= 180.0 - OppositionToleranceDegrees;
    }

    /// <summary>
    /// True when the offset (<paramref name="dx"/>, <paramref name="dy"/>) points
    /// into the outward half-space of a pin with the given world angle — i.e. the
    /// partner sits in front of the pin, not behind or exactly beside it.
    /// </summary>
    private static bool IsInFront(double angleDegrees, double dx, double dy)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        var projection = Math.Cos(radians) * dx + Math.Sin(radians) * dy;
        return projection > MinForwardProjectionUm;
    }
}
