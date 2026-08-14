using CAP_Core.Components.Core;
using Shouldly;

namespace UnitTests.Components;

/// <summary>
/// Core pose math shared by rotation, GDS placement and .lun persistence:
/// 90° quarter turns, exact non-cardinal rotation, horizontal pin mirroring
/// and the "only persist when non-cardinal" helper.
/// </summary>
public class ComponentPoseTransformTests
{
    private const double Tolerance = 1e-9;

    [Fact]
    public void Rotate90CounterClockwise_MovesPinsAndSwapsDimensions()
    {
        // 250×250 coupler: west0 at (0, 80), east1 at (250, 180).
        var component = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins("rot90");

        ComponentPoseTransform.Rotate90CounterClockwise(component);

        // (x, y) → (−y, x) around the center (125, 125).
        var west0 = component.PhysicalPins.First(p => p.Name == "west0");
        west0.OffsetXMicrometers.ShouldBe(170, Tolerance);
        west0.OffsetYMicrometers.ShouldBe(0, Tolerance);
        var east1 = component.PhysicalPins.First(p => p.Name == "east1");
        east1.OffsetXMicrometers.ShouldBe(70, Tolerance);
        east1.OffsetYMicrometers.ShouldBe(250, Tolerance);
        component.RotationDegrees.ShouldBe(90, Tolerance);
    }

    [Fact]
    public void ApplyExactRotation_RotatesPinsByShortestResidual()
    {
        var component = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins("exact330");

        ComponentPoseTransform.ApplyExactRotation(component, 330);

        // 330° from 0° is a −30° residual; west0 (0, 80) rotates around the old
        // center (125, 125) and is re-based into the rotated footprint's AABB —
        // the same frame the GDS projector places non-cardinal instances in.
        var radians = -30 * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var aabbSize = 250 * Math.Abs(cos) + 250 * Math.Abs(sin);
        var expectedX = -125 * cos - -45 * sin + aabbSize / 2;
        var expectedY = -125 * sin + -45 * cos + aabbSize / 2;
        var west0 = component.PhysicalPins.First(p => p.Name == "west0");
        west0.OffsetXMicrometers.ShouldBe(expectedX, Tolerance);
        west0.OffsetYMicrometers.ShouldBe(expectedY, Tolerance);
        component.WidthMicrometers.ShouldBe(aabbSize, Tolerance);
        component.HeightMicrometers.ShouldBe(aabbSize, Tolerance);
        component.UnrotatedWidthMicrometers.ShouldBe(250, Tolerance);
        component.RotationDegrees.ShouldBe(330, Tolerance);
    }

    [Fact]
    public void ApplyExactRotation_MatchesQuarterTurnConvention()
    {
        // Continuous 90° must land the pins exactly where a discrete quarter turn does.
        var exact = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins("exact90");
        var discrete = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins("disc90");

        ComponentPoseTransform.ApplyExactRotation(exact, 90);
        ComponentPoseTransform.Rotate90CounterClockwise(discrete);

        for (int i = 0; i < exact.PhysicalPins.Count; i++)
        {
            exact.PhysicalPins[i].OffsetXMicrometers
                .ShouldBe(discrete.PhysicalPins[i].OffsetXMicrometers, Tolerance);
            exact.PhysicalPins[i].OffsetYMicrometers
                .ShouldBe(discrete.PhysicalPins[i].OffsetYMicrometers, Tolerance);
        }
    }

    [Fact]
    public void ApplyExactRotation_AtCurrentAngle_LeavesPinsUntouched()
    {
        var component = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins("noop");

        ComponentPoseTransform.ApplyExactRotation(component, 0);

        var west0 = component.PhysicalPins.First(p => p.Name == "west0");
        west0.OffsetXMicrometers.ShouldBe(0, Tolerance);
        west0.OffsetYMicrometers.ShouldBe(80, Tolerance);
        component.RotationDegrees.ShouldBe(0, Tolerance);
    }

    [Fact]
    public void MirrorPinsHorizontally_FlipsOffsetsAnglesAndFlag()
    {
        // 250×250 coupler: west0 (0, 80), west1 (0, 180), east0/east1 mirrored right.
        var component = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins("mirror");

        ComponentPoseTransform.MirrorPinsHorizontally(component);

        component.IsMirroredHorizontally.ShouldBeTrue();
        var west0 = component.PhysicalPins.First(p => p.Name == "west0");
        west0.OffsetYMicrometers.ShouldBe(170, Tolerance);
        west0.AngleDegrees.ShouldBe(180, Tolerance);
        var east1 = component.PhysicalPins.First(p => p.Name == "east1");
        east1.OffsetYMicrometers.ShouldBe(70, Tolerance);
        east1.AngleDegrees.ShouldBe(0, Tolerance);
    }

    [Fact]
    public void MirrorPinsHorizontally_Twice_IsIdentity()
    {
        var component = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins("involution");

        ComponentPoseTransform.MirrorPinsHorizontally(component);
        ComponentPoseTransform.MirrorPinsHorizontally(component);

        component.IsMirroredHorizontally.ShouldBeFalse();
        var west0 = component.PhysicalPins.First(p => p.Name == "west0");
        west0.OffsetYMicrometers.ShouldBe(80, Tolerance);
        west0.AngleDegrees.ShouldBe(180, Tolerance);
    }

    [Fact]
    public void GetNonCardinalRotationDegrees_IsNullForCardinalPoses()
    {
        var component = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins("c");
        ComponentPoseTransform.GetNonCardinalRotationDegrees(component).ShouldBeNull();

        ComponentPoseTransform.Rotate90CounterClockwise(component);
        ComponentPoseTransform.GetNonCardinalRotationDegrees(component).ShouldBeNull();
    }

    [Fact]
    public void GetNonCardinalRotationDegrees_ReturnsExactAngleWhenOffCardinal()
    {
        var component = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins("c");
        ComponentPoseTransform.ApplyExactRotation(component, 330);

        ComponentPoseTransform.GetNonCardinalRotationDegrees(component)
            .ShouldNotBeNull()
            .ShouldBe(330, Tolerance);
    }

    [Fact]
    public void Clone_PreservesMirrorFlag()
    {
        var component = TestComponentFactory.CreateFourPinCouplerWithPhysicalPins("c");
        ComponentPoseTransform.MirrorPinsHorizontally(component);

        var clone = (Component)component.Clone();

        clone.IsMirroredHorizontally.ShouldBeTrue();
    }
}
