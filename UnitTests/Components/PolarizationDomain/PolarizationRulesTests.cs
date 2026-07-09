using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Components.PolarizationDomain;

/// <summary>
/// Tests for the TE/TM polarization connection rules, draft-string parsing,
/// and SiEPIC-style name-based inference (issue #534).
/// </summary>
public class PolarizationRulesTests
{
    [Theory]
    [InlineData(PolarizationKind.TE, PolarizationKind.TE, true)]
    [InlineData(PolarizationKind.TM, PolarizationKind.TM, true)]
    [InlineData(PolarizationKind.TE, PolarizationKind.TM, false)]
    [InlineData(PolarizationKind.TM, PolarizationKind.TE, false)]
    [InlineData(PolarizationKind.Both, PolarizationKind.TE, true)]
    [InlineData(PolarizationKind.Both, PolarizationKind.TM, true)]
    [InlineData(PolarizationKind.TE, PolarizationKind.Both, true)]
    [InlineData(PolarizationKind.TM, PolarizationKind.Both, true)]
    [InlineData(PolarizationKind.Both, PolarizationKind.Both, true)]
    public void CanConnect_EnforcesPolarizationCompatibility(
        PolarizationKind a, PolarizationKind b, bool expected)
    {
        PolarizationRules.CanConnect(a, b).ShouldBe(expected);
    }

    [Theory]
    [InlineData("TE", PolarizationKind.TE)]
    [InlineData("TM", PolarizationKind.TM)]
    [InlineData("Both", PolarizationKind.Both)]
    [InlineData("te", PolarizationKind.TE)]
    [InlineData("tm", PolarizationKind.TM)]
    [InlineData("BOTH", PolarizationKind.Both)]
    [InlineData(" TM ", PolarizationKind.TM)]
    [InlineData(null, PolarizationKind.TE)]
    [InlineData("", PolarizationKind.TE)]
    [InlineData("   ", PolarizationKind.TE)]
    public void TryParse_ValidValues_ParsesWithTeDefault(string? value, PolarizationKind expected)
    {
        PolarizationRules.TryParse(value, out var kind).ShouldBeTrue();
        kind.ShouldBe(expected);
    }

    [Theory]
    [InlineData("TEM")]
    [InlineData("circular")]
    [InlineData("42")]
    public void TryParse_InvalidValues_ReturnsFalse(string value)
    {
        PolarizationRules.TryParse(value, out _).ShouldBeFalse();
    }

    [Fact]
    public void Resolve_ExplicitDraftValue_WinsOverNameInference()
    {
        PolarizationRules.Resolve("TE", "GC TM 1550", "GC_TM_1550_8degOxide_BB")
            .ShouldBe(PolarizationKind.TE);
        PolarizationRules.Resolve("Both", "Plain Coupler")
            .ShouldBe(PolarizationKind.Both);
    }

    [Theory]
    [InlineData("GC TM 1550 8deg", "GC_TM_1550_8degOxide_BB", PolarizationKind.TM)]
    [InlineData("Terminator TM 1550", "ebeam_terminator_tm1550", PolarizationKind.TM)]
    [InlineData("Polarizer TM 1550", "ebeam_Polarizer_TM_1550_UQAM", PolarizationKind.TM)]
    [InlineData("Grating Coupler TE 1550", "ebeam_gc_te1550", PolarizationKind.TE)]
    [InlineData("MMI 2x2 Coupler", "pdk.mmi2x2", PolarizationKind.TE)]
    // "Terminator"/"Adiabatic" must not false-positive on embedded letters.
    [InlineData("Terminator 1550", "ebeam_terminator", PolarizationKind.TE)]
    public void Resolve_MissingDraftValue_InfersFromComponentNames(
        string displayName, string nazcaFunction, PolarizationKind expected)
    {
        PolarizationRules.Resolve(null, displayName, nazcaFunction).ShouldBe(expected);
    }

    [Fact]
    public void GetMismatchMessage_NamesBothPinsAndKinds()
    {
        var tePin = new Pin("a0", 0, MatterType.Light, CAP_Core.Tiles.RectSide.Left);
        var tmPin = new Pin("b0", 1, MatterType.Light, CAP_Core.Tiles.RectSide.Right)
        {
            Polarization = PolarizationKind.TM
        };
        var start = new PhysicalPin { Name = "a0", LogicalPin = tePin };
        var end = new PhysicalPin { Name = "b0", LogicalPin = tmPin };

        var message = PolarizationRules.GetMismatchMessage(start, end);

        message.ShouldContain("TE");
        message.ShouldContain("TM");
        message.ShouldContain("a0");
        message.ShouldContain("b0");
    }
}
