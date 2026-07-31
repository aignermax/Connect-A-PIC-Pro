namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// Pure geometry pass behind the GDS import's experimental auto-connect: greedily
/// pairs free pins by proximity when their absolute angles OPPOSE each other
/// (180° ± <see cref="GdsFreePinPairer.OpposingAngleToleranceDegrees"/>). Canvas-free
/// and deterministic — candidates are considered in input order, ties resolve to
/// the earlier candidate, and a pin gets at most one partner. Pins of the SAME
/// placed instance never pair with each other: the pass connects pins BETWEEN
/// instances, not a component's own input to its own output.
/// </summary>
public static class GdsFreePinPairer
{
    /// <summary>
    /// Half-width of the opposition cone: two pins oppose when their angle
    /// difference lies within 180° ± 10° (inclusive edges, i.e. 170°…190°).
    /// </summary>
    public const double OpposingAngleToleranceDegrees = 10.0;

    /// <summary>
    /// Distance margin (µm) below which the two nearest opposing candidates count
    /// as equally near: the pairing is then ambiguous and the pin is skipped
    /// instead of guessing a partner.
    /// </summary>
    public const double AmbiguityDeltaUm = 1.0;

    /// <summary>
    /// Pairs the candidates greedily: every candidate (in input order) takes the
    /// nearest still-free opposing candidate of a DIFFERENT owner within
    /// <paramref name="radiusUm"/> (inclusive), unless no such partner exists or
    /// the two nearest are within <see cref="AmbiguityDeltaUm"/> of each other.
    /// An ambiguous pin is reported and marked unavailable for the rest of the
    /// pass — a reported skip must never end up connected through the back door;
    /// its contenders stay available for other pins.
    /// </summary>
    /// <param name="candidates">Free pins in deterministic (placement) order.</param>
    /// <param name="radiusUm">Maximum pin-to-pin distance for a pair, in µm.</param>
    public static GdsFreePinPairing Pair(IReadOnlyList<GdsFreePinCandidate> candidates, double radiusUm)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var taken = new bool[candidates.Count];
        var pairs = new List<GdsFreePinPair>();
        var skipped = new List<GdsFreePinSkip>();

        for (var i = 0; i < candidates.Count; i++)
        {
            if (taken[i]) continue;
            var origin = candidates[i];

            // Nearest and second-nearest still-free opposing candidates in radius.
            var nearest = -1;
            var second = -1;
            var nearestDist = double.MaxValue;
            var secondDist = double.MaxValue;
            for (var j = 0; j < candidates.Count; j++)
            {
                if (j == i || taken[j]) continue;
                if (candidates[j].OwnerIndex == origin.OwnerIndex) continue;
                var dist = DistanceUm(origin, candidates[j]);
                if (dist > radiusUm) continue;
                if (!AnglesOppose(origin.AngleDegrees, candidates[j].AngleDegrees)) continue;

                if (dist < nearestDist)
                {
                    second = nearest;
                    secondDist = nearestDist;
                    nearest = j;
                    nearestDist = dist;
                }
                else if (dist < secondDist)
                {
                    second = j;
                    secondDist = dist;
                }
            }

            if (nearest < 0)
            {
                skipped.Add(new GdsFreePinSkip(i, GdsFreePinSkipReason.NoOpposingPartnerInRadius));
                continue;
            }
            if (second >= 0 && secondDist - nearestDist < AmbiguityDeltaUm)
            {
                skipped.Add(new GdsFreePinSkip(
                    i, GdsFreePinSkipReason.AmbiguousNearestPartner, nearestDist, secondDist));
                taken[i] = true;
                continue;
            }

            pairs.Add(new GdsFreePinPair(i, nearest, nearestDist));
            taken[i] = taken[nearest] = true;
        }

        return new GdsFreePinPairing(pairs, skipped);
    }

    /// <summary>
    /// True when the angle difference of <paramref name="firstDegrees"/> and
    /// <paramref name="secondDegrees"/> lies in the 180° ± tolerance cone.
    /// </summary>
    private static bool AnglesOppose(double firstDegrees, double secondDegrees) =>
        Math.Abs(Normalize180(firstDegrees - secondDegrees)) >= 180.0 - OpposingAngleToleranceDegrees;

    /// <summary>Normalizes an angle difference to (-180, 180].</summary>
    private static double Normalize180(double degrees)
    {
        var d = degrees % 360.0;
        if (d > 180.0) d -= 360.0;
        if (d <= -180.0) d += 360.0;
        return d;
    }

    private static double DistanceUm(GdsFreePinCandidate a, GdsFreePinCandidate b)
    {
        var dx = b.XUm - a.XUm;
        var dy = b.YUm - a.YUm;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>
/// One free pin considered by <see cref="GdsFreePinPairer"/>: user-presentable
/// label, absolute position (µm), absolute outward angle (degrees, component
/// rotation included), and the index of the placed instance it belongs to
/// (same-instance pins never pair).
/// </summary>
public sealed record GdsFreePinCandidate(
    string Label, double XUm, double YUm, double AngleDegrees, int OwnerIndex);

/// <summary>A matched pair of candidate indexes with their pin-to-pin distance (µm).</summary>
public sealed record GdsFreePinPair(int A, int B, double DistanceUm);

/// <summary>Why a free pin was not paired.</summary>
public enum GdsFreePinSkipReason
{
    /// <summary>No opposing free pin of another instance exists within the pairing radius.</summary>
    NoOpposingPartnerInRadius,

    /// <summary>The two nearest opposing candidates are nearly equidistant, so no partner was chosen.</summary>
    AmbiguousNearestPartner,
}

/// <summary>
/// A free pin that was not paired, with the two nearest candidate distances (µm)
/// for <see cref="GdsFreePinSkipReason.AmbiguousNearestPartner"/> reports.
/// </summary>
public sealed record GdsFreePinSkip(
    int Index,
    GdsFreePinSkipReason Reason,
    double NearestDistanceUm = double.NaN,
    double SecondNearestDistanceUm = double.NaN);

/// <summary>Result of <see cref="GdsFreePinPairer.Pair"/>: matched pairs plus per-pin skip reasons.</summary>
public sealed record GdsFreePinPairing(
    IReadOnlyList<GdsFreePinPair> Pairs,
    IReadOnlyList<GdsFreePinSkip> Skipped);
