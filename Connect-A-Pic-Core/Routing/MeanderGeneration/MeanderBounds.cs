namespace CAP_Core.Routing.MeanderGeneration;

/// <summary>
/// Axis-aligned bounding rectangle (µm) that a generated meander path must stay inside.
/// </summary>
public sealed record MeanderBounds(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>
    /// True when the given tight segment bounds lie inside this rectangle,
    /// allowing <paramref name="slackMicrometers"/> for floating-point noise.
    /// </summary>
    public bool Contains(
        (double MinX, double MinY, double MaxX, double MaxY) segmentBounds,
        double slackMicrometers)
        => segmentBounds.MinX >= MinX - slackMicrometers
        && segmentBounds.MinY >= MinY - slackMicrometers
        && segmentBounds.MaxX <= MaxX + slackMicrometers
        && segmentBounds.MaxY <= MaxY + slackMicrometers;
}
