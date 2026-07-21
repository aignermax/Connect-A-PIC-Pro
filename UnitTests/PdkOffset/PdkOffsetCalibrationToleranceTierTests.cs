using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;

namespace UnitTests.PdkOffset;

/// <summary>
/// Round-5 field report: the adiabatic couplers shipped with a real 0.30 µm
/// pin offset (visible in the GDS export — waveguides are only ~0.5 µm wide)
/// yet the report called them "Aligned" under the single 0.5 µm tolerance.
/// These tests pin the new three-tier verdict:
///   worst ≤ 0.1 µm            → Aligned
///   0.1 µm &lt; worst ≤ 0.5 µm → CheckAlignment (auto-fixable)
///   worst &gt; 0.5 µm           → Misaligned
/// </summary>
public class PdkOffsetCalibrationToleranceTierTests
{
    /// <summary>Draft/render pair whose single pin is off by exactly <paramref name="deltaX"/> µm.</summary>
    private static (PdkComponentDraft draft, NazcaPreviewResult result) BuildWithDelta(double deltaX)
    {
        var draft = new PdkComponentDraft
        {
            Name = "tiered", WidthMicrometers = 10, HeightMicrometers = 10,
            NazcaOriginOffsetX = deltaX, NazcaOriginOffsetY = 10,
            Pins = new() { new() { Name = "in", OffsetXMicrometers = 0, OffsetYMicrometers = 10 } }
        };
        var result = new NazcaPreviewResult
        {
            Success = true, XMin = 0, YMin = 0, XMax = 10, YMax = 10,
            Pins = new List<NazcaPreviewPin> { new() { X = 0, Y = 0 } }
        };
        return (draft, result);
    }

    private static ComponentCheckResult EvaluateTiered(double deltaX)
    {
        var (draft, result) = BuildWithDelta(deltaX);
        return PdkOffsetCalibration.Evaluate(
            draft, result,
            PdkOffsetCalibration.AlignedToleranceMicrometers,
            PdkOffsetCalibration.CheckToleranceMicrometers);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.05)]
    [InlineData(0.1)]    // boundary: ≤ 0.1 is still Aligned
    public void DeltaWithinStrictTolerance_IsAligned(double delta)
    {
        var check = EvaluateTiered(delta);
        check.Status.ShouldBe(ComponentCheckStatus.Aligned, check.Message);
    }

    [Theory]
    [InlineData(0.11)]
    [InlineData(0.3)]    // the adiabatic-coupler field case — no longer "Aligned"
    [InlineData(0.5)]    // boundary: ≤ 0.5 is Check, not Misaligned
    public void DeltaInCheckBand_IsCheckAlignment_AndAutoFixable(double delta)
    {
        var check = EvaluateTiered(delta);
        check.Status.ShouldBe(ComponentCheckStatus.CheckAlignment, check.Message);
        check.IsAutoFixable.ShouldBeTrue();
        check.WorstDeltaMicrometers.ShouldBe(delta, tolerance: 1e-9);
        check.StatusBadge.ShouldBe("≈");
    }

    [Theory]
    [InlineData(0.51)]
    [InlineData(4.46)]   // Contra-Directional Coupler field delta
    [InlineData(9.96)]   // DC Halfring-Straight field delta
    public void DeltaAboveCheckBand_IsMisaligned(double delta)
    {
        var check = EvaluateTiered(delta);
        check.Status.ShouldBe(ComponentCheckStatus.Misaligned, check.Message);
        check.IsAutoFixable.ShouldBeTrue();
    }

    [Fact]
    public void LegacyTwoTierCall_WithoutCheckTolerance_NeverReportsCheckAlignment()
    {
        // Callers that pass only one tolerance keep the historical two-tier
        // semantics: everything above it is Misaligned.
        var (draft, result) = BuildWithDelta(0.3);
        var check = PdkOffsetCalibration.Evaluate(draft, result, 0.5);
        check.Status.ShouldBe(ComponentCheckStatus.Aligned);

        var (draft2, result2) = BuildWithDelta(0.7);
        var check2 = PdkOffsetCalibration.Evaluate(draft2, result2, 0.5);
        check2.Status.ShouldBe(ComponentCheckStatus.Misaligned);
    }

    [Fact]
    public void CheckBandMessage_NamesTheBandAndTheFix()
    {
        var check = EvaluateTiered(0.3);
        check.Message.ShouldContain("check band");
        check.Message.ShouldContain("Auto-Calibrate");
    }
}
