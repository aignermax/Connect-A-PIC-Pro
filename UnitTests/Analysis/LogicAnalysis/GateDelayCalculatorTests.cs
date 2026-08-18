using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation.MaterialDispersion;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// Per-gate propagation delay from the group's internal optical path length
/// (issue #1002): delay = L · n_g / c, with L the internal waveguide lengths plus
/// the leaf component widths and n_g the waveguide group index — from the path's
/// dispersion model when it carries one, else the default silicon group index.
/// </summary>
public class GateDelayCalculatorTests
{
    private const double WavelengthNm = 1550;

    [Fact]
    public void CalculatePicoseconds_StraightPathOnly_ConvertsLengthWithDefaultGroupIndex()
    {
        var group = new ComponentGroup("line");
        group.AddInternalPath(StraightPath(0, 0, 1000, 0));

        var delay = new GateDelayCalculator().CalculatePicoseconds(group, WavelengthNm);

        delay.ShouldBe(
            1000 * GateDelayCalculator.DefaultGroupIndex
            / GateDelayCalculator.SpeedOfLightMicrometersPerPicosecond, 1e-9);
        delay.ShouldBeInRange(10, 20, "~14 ps: physically plausible for 1 mm of silicon waveguide");
    }

    [Fact]
    public void CalculatePicoseconds_CombinerFixture_CountsComponentWidthsPlusInternalPaths()
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();

        var delay = new GateDelayCalculator().CalculatePicoseconds(group, WavelengthNm);

        var expectedLength = GateDelayCalculator.InternalPathLengthMicrometers(group);
        expectedLength.ShouldBe(250, "one coupler 250 µm wide, no internal waveguide paths");
        delay.ShouldBe(
            expectedLength * GateDelayCalculator.DefaultGroupIndex
            / GateDelayCalculator.SpeedOfLightMicrometersPerPicosecond, 1e-9);
    }

    [Fact]
    public void CalculatePicoseconds_MziFixture_SumsAllInternalPathsAndComponentWidths()
    {
        var group = LogicGateFixtureFactory.CreateBalancedMziGroup();

        var length = GateDelayCalculator.InternalPathLengthMicrometers(group);

        var pathLength = group.InternalPaths.Sum(p => p.Path.TotalLengthMicrometers);
        pathLength.ShouldBeGreaterThan(0, "the MZI arms are wired by frozen internal paths");
        length.ShouldBe(pathLength + 2 * 250 + 2 * 100,
            "two couplers (250 µm) and two arm waveguides (100 µm) on top of the paths");
    }

    [Fact]
    public void CalculatePicoseconds_PathWithDispersionModel_UsesItsGroupIndex()
    {
        var group = new ComponentGroup("line");
        var path = StraightPath(0, 0, 1000, 0);
        path.DispersionModel = new ConstantDispersion(groupIndex: 2.0);
        group.AddInternalPath(path);

        var delay = new GateDelayCalculator().CalculatePicoseconds(group, WavelengthNm);

        delay.ShouldBe(1000 * 2.0 / GateDelayCalculator.SpeedOfLightMicrometersPerPicosecond, 1e-9);
    }

    [Fact]
    public void CalculatePicoseconds_NullGroup_Throws() =>
        Should.Throw<ArgumentNullException>(
            () => new GateDelayCalculator().CalculatePicoseconds(null!, WavelengthNm));

    /// <summary>Freezes one straight waveguide path of known geometry inside no pins.</summary>
    private static FrozenWaveguidePath StraightPath(double x1, double y1, double x2, double y2)
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(x1, y1, x2, y2, 0));
        return new FrozenWaveguidePath { Path = path };
    }
}
