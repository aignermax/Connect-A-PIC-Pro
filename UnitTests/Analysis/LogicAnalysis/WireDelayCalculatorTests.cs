using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Connections;
using CAP_Core.LightCalculation.MaterialDispersion;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// Per-wire propagation delay from the connecting waveguide's routed path
/// (issue #1020): delay = L · n_g / c, with n_g from the connection's dispersion
/// model when it carries one, else the default silicon group index — the same
/// convention as <see cref="GateDelayCalculator"/>. Zero-length or unrouted
/// connections contribute zero and never go negative.
/// </summary>
public class WireDelayCalculatorTests
{
    private const double WavelengthNm = 1550;

    [Fact]
    public void CalculatePicoseconds_ThousandMicrometerWaveguide_ConvertsLengthWithDefaultGroupIndex()
    {
        var delay = new WireDelayCalculator().CalculatePicoseconds(StraightConnection(1000), WavelengthNm);

        delay.ShouldBe(
            1000 * GateDelayCalculator.DefaultGroupIndex
            / GateDelayCalculator.SpeedOfLightMicrometersPerPicosecond, 1e-9);
        delay.ShouldBeInRange(14, 15, "~14.01 ps: physically plausible for 1 mm of silicon waveguide");
    }

    [Fact]
    public void CalculatePicoseconds_ConnectionWithDispersionModel_UsesItsOwnGroupIndex()
    {
        var connection = StraightConnection(1000);
        connection.DispersionModel = new ConstantDispersion(groupIndex: 2.0);

        var delay = new WireDelayCalculator().CalculatePicoseconds(connection, WavelengthNm);

        delay.ShouldBe(1000 * 2.0 / GateDelayCalculator.SpeedOfLightMicrometersPerPicosecond, 1e-9);
    }

    [Fact]
    public void CalculatePicoseconds_ConnectionWithoutRoutedPath_ContributesZeroDelay()
    {
        var delay = new WireDelayCalculator().CalculatePicoseconds(new WaveguideConnection(), WavelengthNm);

        delay.ShouldBe(0, "direct-adjacent pins have no waveguide length to cross");
        delay.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void CalculatePicoseconds_NullConnection_Throws() =>
        Should.Throw<ArgumentNullException>(
            () => new WireDelayCalculator().CalculatePicoseconds(null!, WavelengthNm));

    /// <summary>A connection whose routed path is one straight segment of known length.</summary>
    private static WaveguideConnection StraightConnection(double lengthMicrometers)
    {
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(0, 0, lengthMicrometers, 0, 0));
        var connection = new WaveguideConnection();
        connection.ReplaceRoutedPath(path);
        return connection;
    }
}
