using CAP_Core.Components.Core;

namespace CAP_Core.Routing.CrossingInsertion.ManualCrossing;

/// <summary>
/// A guide line for the manual Cut tool: a ray starting at a
/// component pin and extending outward along the pin's facing axis. Where such
/// a ray crosses a perpendicular waveguide segment, a crossing component can be
/// inserted so a future connection from the pin can pass straight through.
/// </summary>
/// <param name="Pin">The physical pin the guide line originates from.</param>
/// <param name="Origin">Absolute position of the pin in micrometers.</param>
/// <param name="Direction">Unit direction of the ray (axis-aligned, pin facing direction).</param>
/// <param name="IsHorizontal">True when the ray runs along the X axis.</param>
public sealed record PinGuideLine(
    PhysicalPin Pin,
    (double X, double Y) Origin,
    (double X, double Y) Direction,
    bool IsHorizontal);
