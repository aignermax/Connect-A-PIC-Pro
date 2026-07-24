using System.Linq;
using Avalonia;
using CAP.Avalonia.Controls.Canvas.ComponentPreview;
using Shouldly;
using Xunit;

namespace UnitTests.Canvas.ComponentPreview;

/// <summary>
/// Unit tests for <see cref="GdsPolygonRenderer"/> coordinate transform math.
/// Tests the pure functions without needing a rendering context.
/// </summary>
public sealed class GdsPolygonRendererTests
{
    // ── TransformVertex ─────────────────────────────────────────────────────

    [Fact]
    public void TransformVertex_OriginVertex_MapsToComponentOrigin()
    {
        // Nazca bbox origin (XMin, YMin) should map to component canvas origin (compX, compY + compHeight)
        // Because Y is flipped: canvasY = compY + (yMax - nazcaY) * scaleY
        // At nazcaY = yMin and nazcaX = xMin: canvasX = compX, canvasY = compY + bboxH * scaleY = compY + compHeight
        var (cx, cy) = GdsPolygonRenderer.TransformVertex(
            nazcaX: 0, nazcaY: 0,     // bbox origin in Nazca space
            xMin: 0, yMax: 10,        // bbox: x in [0,10], y in [0,10]
            scaleX: 2, scaleY: 2,     // 2× scale
            compX: 5, compY: 5);

        cx.ShouldBe(5.0);             // compX + 0 * scaleX
        cy.ShouldBe(25.0);            // compY + (yMax - 0) * scaleY = 5 + 10*2
    }

    [Fact]
    public void TransformVertex_TopRightNazca_MapsToTopRightCanvas()
    {
        // Top-right in Nazca (xMax, yMax) should map to top-right of component rect
        var (cx, cy) = GdsPolygonRenderer.TransformVertex(
            nazcaX: 10, nazcaY: 10,
            xMin: 0, yMax: 10,
            scaleX: 1, scaleY: 1,
            compX: 0, compY: 0);

        cx.ShouldBe(10.0);   // compX + (xMax - xMin) * scaleX
        cy.ShouldBe(0.0);    // compY + (yMax - yMax) * scaleY = 0
    }

    [Fact]
    public void TransformVertex_BottomLeftNazca_MapsToBottomLeftCanvas()
    {
        // Bottom-left in Nazca (xMin, yMin) should map to bottom-left of component rect
        var (cx, cy) = GdsPolygonRenderer.TransformVertex(
            nazcaX: 2, nazcaY: 3,   // xMin = 2, yMin = 3
            xMin: 2, yMax: 8,       // bbox: x in [2,?], y in [3,8]
            scaleX: 3, scaleY: 2,
            compX: 10, compY: 20);

        cx.ShouldBe(10.0);   // compX + (2-2)*3
        cy.ShouldBe(30.0);   // compY + (8-3)*2 = 20 + 10
    }

    [Fact]
    public void TransformVertex_NonZeroOffset_CorrectlyNormalisesOrigin()
    {
        // A Nazca component with non-zero bbox origin should normalise correctly
        var (cx, cy) = GdsPolygonRenderer.TransformVertex(
            nazcaX: 5, nazcaY: 5,
            xMin: 5, yMax: 10,
            scaleX: 1, scaleY: 1,
            compX: 0, compY: 0);

        cx.ShouldBe(0.0);   // normalised to origin
        cy.ShouldBe(5.0);   // yMax - nazcaY = 10 - 5 = 5
    }

    [Fact]
    public void TransformVertex_MidpointNazca_MapsMidpointCanvas()
    {
        // Midpoint of Nazca bbox should map to midpoint of canvas component
        var (cx, cy) = GdsPolygonRenderer.TransformVertex(
            nazcaX: 5, nazcaY: 5,     // midpoint of [0,10]×[0,10] bbox
            xMin: 0, yMax: 10,
            scaleX: 1, scaleY: 1,
            compX: 0, compY: 0);

        cx.ShouldBe(5.0);
        cy.ShouldBe(5.0);
    }

    // ── BuildRotationMatrix ─────────────────────────────────────────────────

    [Fact]
    public void BuildRotationMatrix_ZeroDegrees_ReturnsIdentity()
    {
        var m = GdsPolygonRenderer.BuildRotationMatrix(0, 50, 50);
        m.ShouldBe(Matrix.Identity);
    }

    [Fact]
    public void BuildRotationMatrix_90Degrees_RotatesAroundCenter()
    {
        // A 90° rotation around (10, 10):
        // Point (20, 10) → rotated 90° CW around (10,10) → (10, 20)
        var m = GdsPolygonRenderer.BuildRotationMatrix(90, 10, 10);

        // Transform point (20, 10) relative to pivot
        var original = new Point(20, 10);
        var transformed = original.Transform(m);

        transformed.X.ShouldBe(10.0, tolerance: 1e-9);
        transformed.Y.ShouldBe(20.0, tolerance: 1e-9);
    }

    [Fact]
    public void BuildRotationMatrix_180Degrees_FlipsBothAxes()
    {
        // 180° around (10, 10): (20, 10) → (0, 10)
        var m = GdsPolygonRenderer.BuildRotationMatrix(180, 10, 10);
        var transformed = new Point(20, 10).Transform(m);

        transformed.X.ShouldBe(0.0, tolerance: 1e-9);
        transformed.Y.ShouldBe(10.0, tolerance: 1e-9);
    }

    [Fact]
    public void BuildRotationMatrix_PivotIsMappedToItself()
    {
        // The pivot point (cx, cy) must be invariant under any rotation
        var m = GdsPolygonRenderer.BuildRotationMatrix(45, 15, 25);
        var pivot = new Point(15, 25).Transform(m);

        pivot.X.ShouldBe(15.0, tolerance: 1e-9);
        pivot.Y.ShouldBe(25.0, tolerance: 1e-9);
    }

    // ── GetUnrotatedSize ────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 50, 100, 50, 100)]
    [InlineData(90, 50, 100, 100, 50)]    // rotate command swapped dims → swap back
    [InlineData(180, 50, 100, 50, 100)]
    [InlineData(270, 50, 100, 100, 50)]
    [InlineData(360, 50, 100, 50, 100)]
    [InlineData(-90, 50, 100, 100, 50)]
    public void GetUnrotatedSize_SwapsBackAtOddQuarterTurns(
        double rotationDegrees, double currentW, double currentH, double expectedW, double expectedH)
    {
        var (w, h) = GdsPolygonRenderer.GetUnrotatedSize(rotationDegrees, currentW, currentH);
        w.ShouldBe(expectedW);
        h.ShouldBe(expectedH);
    }

    // ── GetUnrotatedDestRect ────────────────────────────────────────────────

    [Fact]
    public void GetUnrotatedDestRect_90Degrees_IsUnrotatedSizeCenteredOnFootprint()
    {
        // Footprint after one R press: 50 wide × 100 tall (dims already swapped by the
        // rotate command). The unrotated bitmap (100×50) must be centred on the footprint
        // centre (25, 50) so the rotation transform maps it back onto the footprint.
        var rect = GdsPolygonRenderer.GetUnrotatedDestRect(
            compX: 0, compY: 0, compWidth: 50, compHeight: 100, rotationDegrees: 90);

        rect.X.ShouldBe(-25.0, tolerance: 1e-9);
        rect.Y.ShouldBe(25.0, tolerance: 1e-9);
        rect.Width.ShouldBe(100.0, tolerance: 1e-9);
        rect.Height.ShouldBe(50.0, tolerance: 1e-9);
    }

    [Fact]
    public void GetUnrotatedDestRect_ZeroDegrees_EqualsFootprint()
    {
        var rect = GdsPolygonRenderer.GetUnrotatedDestRect(
            compX: 10, compY: 20, compWidth: 50, compHeight: 100, rotationDegrees: 0);

        rect.ShouldBe(new Rect(10, 20, 50, 100));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void GetUnrotatedDestRect_RotatedByRotationMatrix_CoversComponentFootprint(double rotationDegrees)
    {
        // Invariant behind the fix for the "preview stays unrotated" bug: drawing the
        // unrotated bitmap into GetUnrotatedDestRect and then applying BuildRotationMatrix
        // (exactly what DrawGdsPreview does) must land the image precisely on the
        // component's current, rotation-swapped footprint — for a NON-square component.
        const double compX = 10, compY = 20, compW = 50, compH = 100;
        double centerX = compX + compW / 2.0;
        double centerY = compY + compH / 2.0;

        var dest = GdsPolygonRenderer.GetUnrotatedDestRect(compX, compY, compW, compH, rotationDegrees);
        var m = GdsPolygonRenderer.BuildRotationMatrix(rotationDegrees, centerX, centerY);

        var corners = new[]
        {
            dest.TopLeft.Transform(m),
            dest.TopRight.Transform(m),
            dest.BottomLeft.Transform(m),
            dest.BottomRight.Transform(m),
        };
        double minX = corners.Min(p => p.X);
        double minY = corners.Min(p => p.Y);
        double maxX = corners.Max(p => p.X);
        double maxY = corners.Max(p => p.Y);

        minX.ShouldBe(compX, tolerance: 1e-9);
        minY.ShouldBe(compY, tolerance: 1e-9);
        maxX.ShouldBe(compX + compW, tolerance: 1e-9);
        maxY.ShouldBe(compY + compH, tolerance: 1e-9);
    }
}
