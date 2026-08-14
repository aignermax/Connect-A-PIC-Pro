using CAP_Core.Components.Core;
using CAP_Core.Components.PinKinds;

namespace CAP_Core.Routing.AutoConnect;

/// <summary>
/// A pin offered to <see cref="FacingPinPairFinder"/> with its precomputed
/// world-space position and outward angle, so the pairing math stays pure and
/// testable without a placed component graph.
/// </summary>
/// <param name="Pin">The physical pin this candidate represents.</param>
/// <param name="X">Absolute X position of the pin (µm).</param>
/// <param name="Y">Absolute Y position of the pin (µm, Y-down app space).</param>
/// <param name="AngleDegrees">Outward world-space angle of the pin (0° = +X, 90° = +Y).</param>
/// <param name="IsElectrical">True for a metal contact, false for an optical port.</param>
public sealed record FacingPinCandidate(
    PhysicalPin Pin, double X, double Y, double AngleDegrees, bool IsElectrical)
{
    /// <summary>Builds a candidate from a placed pin's absolute position and angle.</summary>
    public static FacingPinCandidate FromPin(PhysicalPin pin)
    {
        var (x, y) = pin.GetAbsolutePosition();
        return new FacingPinCandidate(pin, x, y, pin.GetAbsoluteAngle(), PinKindHelper.IsElectrical(pin));
    }
}

/// <summary>A matched pair of mutually facing pins with their straight-line distance.</summary>
/// <param name="A">First pin of the pair.</param>
/// <param name="B">Second pin of the pair.</param>
/// <param name="DistanceUm">Straight-line pin-to-pin distance (µm).</param>
public sealed record FacingPinPair(PhysicalPin A, PhysicalPin B, double DistanceUm);

/// <summary>Result of a <see cref="FacingPinPairFinder.FindPairs"/> run.</summary>
/// <param name="Pairs">Matched pairs, nearest-first.</param>
/// <param name="UnpairedPins">Candidates for which no facing partner exists.</param>
public sealed record FacingPinPairResult(
    IReadOnlyList<FacingPinPair> Pairs, IReadOnlyList<PhysicalPin> UnpairedPins);
