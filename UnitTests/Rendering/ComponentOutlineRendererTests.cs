using Avalonia;
using CAP.Avalonia.Controls.Rendering;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Rendering;

/// <summary>
/// Unit tests for the outline-polygon transform math of ComponentOutlineRenderer:
/// local outline points (µm, Y-down, relative to the unrotated bbox top-left) must
/// land on the same world positions as pins rotated by the rotate command — the
/// renderer reuses the GdsPolygonRenderer rotation mechanism to guarantee that.
/// Pure-math tests only; StreamGeometry building needs a render platform.
/// </summary>
public class ComponentOutlineRendererTests
{
    private const double Tolerance = 1e-9;

    private static OutlinePolygon RectPolygon(double x0, double y0, double x1, double y1) => new()
    {
        Layer = 1,
        DataType = 0,
        Points = new[]
        {
            new OutlinePoint(x0, y0), new OutlinePoint(x1, y0),
            new OutlinePoint(x1, y1), new OutlinePoint(x0, y1),
            new OutlinePoint(x0, y0) // closed ring: first point repeated at the end
        }
    };

    [Fact]
    public void TransformOutlinePoint_NoRotation_MapsLocalOriginToComponentOrigin()
    {
        var world = ComponentOutlineRenderer.TransformOutlinePoint(
            new OutlinePoint(0, 0),
            compX: 100, compY: 50, compWidth: 20, compHeight: 10, rotationDegrees: 0);

        world.X.ShouldBe(100.0, Tolerance);
        world.Y.ShouldBe(50.0, Tolerance);
    }

    [Fact]
    public void TransformOutlinePoint_NoRotation_MapsLocalPointOneToOneInMicrometers()
    {
        var world = ComponentOutlineRenderer.TransformOutlinePoint(
            new OutlinePoint(5, 3),
            compX: 100, compY: 50, compWidth: 20, compHeight: 10, rotationDegrees: 0);

        world.X.ShouldBe(105.0, Tolerance);
        world.Y.ShouldBe(53.0, Tolerance);
    }

    [Fact]
    public void TransformOutlinePoint_90Degrees_MatchesPinRotationAroundCenter()
    {
        // Unrotated footprint 20×10 at (100,50); after one rotate the live dims are
        // swapped to 10×20. The pin at unrotated offset (20,0) lands — via the rotate
        // command's offset math — at absolute (110,70); the outline point must agree.
        var world = ComponentOutlineRenderer.TransformOutlinePoint(
            new OutlinePoint(20, 0),
            compX: 100, compY: 50, compWidth: 10, compHeight: 20, rotationDegrees: 90);

        world.X.ShouldBe(110.0, Tolerance);
        world.Y.ShouldBe(70.0, Tolerance);
    }

    [Fact]
    public void TransformOutlinePoint_90Degrees_LocalOriginFollowsPinMath()
    {
        // Same pose as above; pin at unrotated offset (0,0) lands at absolute (110,50).
        var world = ComponentOutlineRenderer.TransformOutlinePoint(
            new OutlinePoint(0, 0),
            compX: 100, compY: 50, compWidth: 10, compHeight: 20, rotationDegrees: 90);

        world.X.ShouldBe(110.0, Tolerance);
        world.Y.ShouldBe(50.0, Tolerance);
    }

    [Fact]
    public void TransformOutlinePoint_180Degrees_MapsTopLeftToBottomRight()
    {
        // 180° restores the original dims (swapped twice), so the footprint is 20×10.
        var world = ComponentOutlineRenderer.TransformOutlinePoint(
            new OutlinePoint(0, 0),
            compX: 100, compY: 50, compWidth: 20, compHeight: 10, rotationDegrees: 180);

        world.X.ShouldBe(120.0, Tolerance);
        world.Y.ShouldBe(60.0, Tolerance);
    }

    [Fact]
    public void TransformOutlinePoint_270Degrees_MatchesQuarterTurnBack()
    {
        // 270° ≡ one counter-quarter-turn; local top-right (20,0) lands at the
        // footprint's top-left corner (100,50).
        var world = ComponentOutlineRenderer.TransformOutlinePoint(
            new OutlinePoint(20, 0),
            compX: 100, compY: 50, compWidth: 10, compHeight: 20, rotationDegrees: 270);

        world.X.ShouldBe(100.0, Tolerance);
        world.Y.ShouldBe(50.0, Tolerance);
    }

    [Fact]
    public void ComputeWorldPoints_KeepsRingClosed()
    {
        var world = ComponentOutlineRenderer.ComputeWorldPoints(
            RectPolygon(0, 4, 20, 6),
            compX: 100, compY: 50, compWidth: 20, compHeight: 10, rotationDegrees: 0);

        world.Length.ShouldBe(5);
        world[0].ShouldBe(new Point(100, 54));
        world[1].ShouldBe(new Point(120, 54));
        world[2].ShouldBe(new Point(120, 56));
        world[3].ShouldBe(new Point(100, 56));
        // Closing point handling: the repeated first vertex closes the ring in world space too.
        world[4].ShouldBe(world[0]);
    }

    [Fact]
    public void ComputeWorldPoints_90Degrees_RotatesEveryVertex()
    {
        // The whole unrotated 20×10 rect, rotated 90° (live dims 10×20 at (100,50)):
        // every vertex must land inside/on the rotated footprint exactly.
        var world = ComponentOutlineRenderer.ComputeWorldPoints(
            RectPolygon(0, 0, 20, 10),
            compX: 100, compY: 50, compWidth: 10, compHeight: 20, rotationDegrees: 90);

        AssertVertex(world[0], 110, 50);  // (0,0)
        AssertVertex(world[1], 110, 70);  // (20,0)
        AssertVertex(world[2], 100, 70);  // (20,10)
        AssertVertex(world[3], 100, 50);  // (0,10)
        world[4].X.ShouldBe(world[0].X, Tolerance);
        world[4].Y.ShouldBe(world[0].Y, Tolerance);
    }

    [Fact]
    public void TransformOutlinePoint_NonCardinalRotation_MatchesModelPinMath()
    {
        // 20×10 unrotated footprint; after a 30° model-level rotation the live
        // dims are the rotated AABB. The outline transform of a local point must
        // equal the pin-offset rotation of the same point — the renderer gets
        // the unrotated frame from the recorded pre-rotation dims (without them
        // it guesses the AABB as the frame and the body renders offset by half
        // the AABB-vs-original size difference — the field-report Y drift).
        const double unrotW = 20, unrotH = 10, degrees = 30;
        double rad = degrees * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double newW = (unrotW * Math.Abs(cos)) + (unrotH * Math.Abs(sin));
        double newH = (unrotW * Math.Abs(sin)) + (unrotH * Math.Abs(cos));
        const double compX = 100, compY = 50;

        var local = new OutlinePoint(20, 0);
        // Model-level pin math (RotateComponentCommand.ApplyModelRotation):
        // rotate around the old center, re-base into the new AABB.
        double lx = local.X - unrotW / 2, ly = local.Y - unrotH / 2;
        double expectedX = compX + (newW / 2) + (lx * cos) - (ly * sin);
        double expectedY = compY + (newH / 2) + (lx * sin) + (ly * cos);

        var world = ComponentOutlineRenderer.TransformOutlinePoint(
            local, compX, compY, newW, newH, degrees,
            recordedUnrotatedWidth: unrotW, recordedUnrotatedHeight: unrotH);

        world.X.ShouldBe(expectedX, Tolerance);
        world.Y.ShouldBe(expectedY, Tolerance);
    }

    [Fact]
    public void TransformOutlinePoint_NonCardinalWithoutRecordedDims_FallsBackToLegacyGuess()
    {
        // Backward compatibility: a component that never recorded its dims
        // (0/0) keeps the legacy behavior — the renderer cannot know better.
        var withDims = ComponentOutlineRenderer.TransformOutlinePoint(
            new OutlinePoint(20, 0), 100, 50, 22.32050807568877, 18.660254037844386, 30, 20, 10);
        var legacy = ComponentOutlineRenderer.TransformOutlinePoint(
            new OutlinePoint(20, 0), 100, 50, 22.32050807568877, 18.660254037844386, 30);

        double driftX = withDims.X - legacy.X;
        double driftY = withDims.Y - legacy.Y;
        Math.Sqrt((driftX * driftX) + (driftY * driftY)).ShouldBeGreaterThan(1.0,
            "without the recorded unrotated dims the legacy guess visibly misplaces the body");
    }

    private static void AssertVertex(Point actual, double expectedX, double expectedY)
    {
        actual.X.ShouldBe(expectedX, Tolerance);
        actual.Y.ShouldBe(expectedY, Tolerance);
    }
}
