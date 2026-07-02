namespace CAP_Core.Routing.AStarPathfinder;

/// <summary>
/// Represents a direction of travel on the pathfinding grid.
/// Uses 8 directions in 45° steps (octile routing with diagonals).
/// Enum values are ordered counter-clockwise so that one enum step equals 45°.
/// </summary>
public enum GridDirection
{
    None = -1,
    East = 0,       // 0 degrees (pointing right)
    NorthEast = 1,  // 45 degrees
    North = 2,      // 90 degrees (pointing up)
    NorthWest = 3,  // 135 degrees
    West = 4,       // 180 degrees (pointing left)
    SouthWest = 5,  // 225 degrees
    South = 6,      // 270 degrees (pointing down)
    SouthEast = 7   // 315 degrees
}

/// <summary>
/// Extension methods for GridDirection.
/// </summary>
public static class GridDirectionExtensions
{
    /// <summary>
    /// Number of discrete directions (45° sectors).
    /// </summary>
    public const int DirectionCount = 8;

    /// <summary>
    /// Angular step between adjacent directions in degrees.
    /// </summary>
    public const double AngleStepDegrees = 45.0;

    private static readonly (int dx, int dy)[] Deltas =
    {
        (1, 0),   // East
        (1, 1),   // NorthEast
        (0, 1),   // North
        (-1, 1),  // NorthWest
        (-1, 0),  // West
        (-1, -1), // SouthWest
        (0, -1),  // South
        (1, -1)   // SouthEast
    };

    /// <summary>
    /// Gets the grid delta (dx, dy) for moving in this direction.
    /// </summary>
    public static (int dx, int dy) GetDelta(this GridDirection dir)
    {
        if (dir == GridDirection.None)
            return (0, 0);
        return Deltas[(int)dir];
    }

    /// <summary>
    /// Checks whether this direction is a 45° diagonal (NE, NW, SE, SW).
    /// </summary>
    public static bool IsDiagonal(this GridDirection dir)
    {
        return dir != GridDirection.None && (int)dir % 2 == 1;
    }

    /// <summary>
    /// Gets the angle in degrees for this direction (0=East, 45=NorthEast, ...).
    /// </summary>
    public static double GetAngleDegrees(this GridDirection dir)
    {
        if (dir == GridDirection.None)
            return 0;
        return (int)dir * AngleStepDegrees;
    }

    /// <summary>
    /// Creates a GridDirection from an angle in degrees.
    /// Rounds to the nearest of the 8 directions (45° sectors).
    /// </summary>
    public static GridDirection FromAngle(double degrees)
    {
        // Normalize to 0-360
        while (degrees < 0) degrees += 360;
        while (degrees >= 360) degrees -= 360;

        int sector = (int)Math.Round(degrees / AngleStepDegrees) % DirectionCount;
        return (GridDirection)sector;
    }

    /// <summary>
    /// Gets the turn angle in degrees between two directions.
    /// Returns a multiple of 45 in the range (-180, 180].
    /// </summary>
    public static double GetTurnAngle(GridDirection from, GridDirection to)
    {
        if (from == GridDirection.None || to == GridDirection.None)
            return 0;

        int diff = (int)to - (int)from;

        // Normalize to (-4, 4] range (represents (-180, 180] degrees in 45° steps)
        if (diff > DirectionCount / 2) diff -= DirectionCount;
        if (diff <= -DirectionCount / 2) diff += DirectionCount;

        return diff * AngleStepDegrees;
    }

    /// <summary>
    /// Gets the opposite direction (180° rotation).
    /// </summary>
    public static GridDirection GetOpposite(this GridDirection dir)
    {
        if (dir == GridDirection.None)
            return GridDirection.None;
        return (GridDirection)(((int)dir + DirectionCount / 2) % DirectionCount);
    }

    /// <summary>
    /// Returns all 8 directions (cardinals and diagonals).
    /// </summary>
    public static GridDirection[] GetAllDirections()
    {
        return new[]
        {
            GridDirection.East, GridDirection.NorthEast,
            GridDirection.North, GridDirection.NorthWest,
            GridDirection.West, GridDirection.SouthWest,
            GridDirection.South, GridDirection.SouthEast
        };
    }
}
