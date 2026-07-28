using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;

namespace CAP_Core.Routing.CrossingInsertion;

/// <summary>
/// Places a real PDK crossing component (e.g. ebeam_crossing4) at a validated
/// intersection point and splits the two crossed connections into four
/// sub-connections that dock onto the crossing's ports. The crossing is a normal
/// component with its own S-matrix, so through-loss and crosstalk flow into the
/// light simulation without any special-case loss model.
/// </summary>
public class CrossingPlacement
{
    /// <summary>Tolerance when matching a physical pin by its absolute angle (degrees).</summary>
    private const double PinAngleToleranceDegrees = 1.0;

    /// <summary>Absolute facing angle of the crossing's east port (app coordinates).</summary>
    public const double EastAngleDegrees = 0;

    /// <summary>Absolute facing angle of the crossing's south port (app coordinates).</summary>
    public const double SouthAngleDegrees = 90;

    /// <summary>Absolute facing angle of the crossing's west port (app coordinates).</summary>
    public const double WestAngleDegrees = 180;

    /// <summary>Absolute facing angle of the crossing's north port (app coordinates).</summary>
    public const double NorthAngleDegrees = 270;

    /// <summary>
    /// Centers the crossing component on the intersection point, maps its four ports
    /// to the two crossed connections by travel direction, and creates the four
    /// sub-connections (inheriting each original's loss parameters).
    /// </summary>
    /// <param name="candidate">The validated crossing candidate.</param>
    /// <param name="crossingComponent">A fresh crossing component instance (never reused).</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the component lacks one of the four axis-facing ports with a linked
    /// logical pin — inserting it silently would break the S-matrix wiring.
    /// </exception>
    public CrossingRecord Place(CrossingCandidate candidate, Component crossingComponent)
    {
        var point = candidate.IntersectionPoint;
        crossingComponent.PhysicalX = point.X - crossingComponent.WidthMicrometers / 2.0;
        crossingComponent.PhysicalY = point.Y - crossingComponent.HeightMicrometers / 2.0;

        var (horizontal, horizontalDirection, vertical, verticalDirection) =
            candidate.NewConnectionIsHorizontal
                ? (candidate.NewConnection, candidate.NewDirection,
                   candidate.ExistingConnection, candidate.ExistingDirection)
                : (candidate.ExistingConnection, candidate.ExistingDirection,
                   candidate.NewConnection, candidate.NewDirection);

        var (horizontalEntry, horizontalExit) = RequireThroughPorts(crossingComponent, true, horizontalDirection);
        var (verticalEntry, verticalExit) = RequireThroughPorts(crossingComponent, false, verticalDirection);

        var subsHorizontal = new List<WaveguideConnection>
        {
            CreateSubConnection(horizontal, horizontal.StartPin, horizontalEntry),
            CreateSubConnection(horizontal, horizontalExit, horizontal.EndPin),
        };
        var subsVertical = new List<WaveguideConnection>
        {
            CreateSubConnection(vertical, vertical.StartPin, verticalEntry),
            CreateSubConnection(vertical, verticalExit, vertical.EndPin),
        };

        bool newIsHorizontal = candidate.NewConnectionIsHorizontal;
        return new CrossingRecord
        {
            CrossingComponent = crossingComponent,
            OriginalA = candidate.NewConnection,
            OriginalB = candidate.ExistingConnection,
            SubConnectionsA = newIsHorizontal ? subsHorizontal : subsVertical,
            SubConnectionsB = newIsHorizontal ? subsVertical : subsHorizontal,
            AnchorPositions = CrossingRecord.CaptureAnchors(
                candidate.NewConnection, candidate.ExistingConnection),
        };
    }

    /// <summary>
    /// Finds the physical pin whose absolute angle matches the given value
    /// (0 = east, 90 = south, 180 = west, 270 = north in app coordinates).
    /// </summary>
    public static PhysicalPin? FindPinByAngle(Component component, double angleDegrees)
    {
        return component.PhysicalPins.FirstOrDefault(pin =>
            AngleDifference(pin.GetAbsoluteAngle(), angleDegrees) <= PinAngleToleranceDegrees);
    }

    /// <summary>
    /// Resolves the crossing ports a connection travelling in <paramref name="direction"/>
    /// docks onto: the entry/exit pair along its own axis (horizontal → west/east, vertical →
    /// north/south), oriented by travel direction (+X enters west, +Y enters north in app
    /// coordinates). Shared by <see cref="Place"/> (two connections crossing) and the Cut
    /// tool's manual split (one connection docked onto the same crossing).
    /// </summary>
    public static (PhysicalPin? Entry, PhysicalPin? Exit) ResolveThroughPorts(
        Component crossingComponent, bool isHorizontal, (double X, double Y) direction)
    {
        var (entryAngle, exitAngle) = EntryExitAngles(isHorizontal, direction);
        return (FindPinByAngle(crossingComponent, entryAngle), FindPinByAngle(crossingComponent, exitAngle));
    }

    /// <summary>
    /// Creates a sub-connection docked onto a crossing port, inheriting the original
    /// connection's waveguide parameters. Shared by <see cref="Place"/> and the Cut tool's
    /// manual split so both produce identically-configured stubs.
    /// </summary>
    public static WaveguideConnection CreateSubConnection(
        WaveguideConnection original, PhysicalPin startPin, PhysicalPin endPin)
    {
        return new WaveguideConnection
        {
            StartPin = startPin,
            EndPin = endPin,
            WidthMicrometers = original.WidthMicrometers,
            BendRadiusMicrometers = original.BendRadiusMicrometers,
            PropagationLossDbPerCm = original.PropagationLossDbPerCm,
            BendLossDbPer90Deg = original.BendLossDbPer90Deg,
            DispersionModel = original.DispersionModel,
        };
    }

    private static (PhysicalPin Entry, PhysicalPin Exit) RequireThroughPorts(
        Component crossingComponent, bool isHorizontal, (double X, double Y) direction)
    {
        var (entryAngle, exitAngle) = EntryExitAngles(isHorizontal, direction);
        return (RequirePin(crossingComponent, entryAngle), RequirePin(crossingComponent, exitAngle));
    }

    private static (double EntryAngle, double ExitAngle) EntryExitAngles(
        bool isHorizontal, (double X, double Y) direction)
    {
        double axisSign = isHorizontal ? direction.X : direction.Y;
        if (isHorizontal)
            return axisSign > 0 ? (WestAngleDegrees, EastAngleDegrees) : (EastAngleDegrees, WestAngleDegrees);
        return axisSign > 0 ? (NorthAngleDegrees, SouthAngleDegrees) : (SouthAngleDegrees, NorthAngleDegrees);
    }

    private static PhysicalPin RequirePin(Component component, double angleDegrees)
    {
        var pin = FindPinByAngle(component, angleDegrees);
        if (pin == null || pin.LogicalPin == null)
        {
            throw new InvalidOperationException(
                $"Crossing component '{component.Name}' has no port facing {angleDegrees}° " +
                "with a linked logical pin — cannot wire the crossing into the S-matrix.");
        }
        return pin;
    }

    private static double AngleDifference(double a, double b)
    {
        double diff = Math.Abs(a - b) % 360.0;
        return diff > 180.0 ? 360.0 - diff : diff;
    }
}
