using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_Core.Routing.CrossingInsertion;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.CrossingInsertion;

/// <summary>
/// Validates the crossing-insertion assumptions against the REAL
/// "Crossing 4-Port" (ebeam_crossing4) entry of the bundled
/// siepic-ebeam-pdk.json, instantiated through the production PDK path
/// (PdkLoader → PdkTemplateConverter → ComponentTemplates.CreateFromTemplate)
/// instead of a hand-replicated test double.
/// </summary>
public class CrossingPdkComponentTests
{
    private const string SiepicPdkFile = "siepic-ebeam-pdk.json";
    private const string CrossingNazcaFunction = "ebeam_crossing4";

    /// <summary>
    /// -20·log10(0.978) — through-loss of the PDK crossing at 1550 nm. The through
    /// amplitude was corrected from 0.98 to 0.978 in field round 4 (final batch): with
    /// the 0.02 crosstalk and 0.01 reflection parasitics the hand-authored matrix
    /// exceeded passivity (σ_max = 1.0014) and fabricated energy in every simulation.
    /// </summary>
    private const double ExpectedThroughLossDb = 0.1932;

    private static ComponentTemplate LoadCrossingTemplate()
    {
        var templates = TestPdkLoader.LoadFromPdk(SiepicPdkFile);
        templates.ShouldNotBeEmpty($"{SiepicPdkFile} must be bundled with the test output");
        return templates.Single(t => string.Equals(
            t.NazcaFunctionName, CrossingNazcaFunction, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Production factory: instantiates the crossing exactly like a placed PDK component.</summary>
    private static Component CreateFromRealPdk() =>
        ComponentTemplates.CreateFromTemplate(LoadCrossingTemplate(), 0, 0);

    [Fact]
    public void RealPdkCrossing_HasFourAxisAlignedPortsWithLogicalPins()
    {
        var crossing = CreateFromRealPdk();

        crossing.WidthMicrometers.ShouldBe(9.7, tolerance: 0.01);
        crossing.HeightMicrometers.ShouldBe(9.7, tolerance: 0.01);

        // CrossingPlacement.RequirePin assumes exactly these four port angles.
        foreach (var angle in new[] { 0.0, 90.0, 180.0, 270.0 })
        {
            var pin = CrossingPlacement.FindPinByAngle(crossing, angle);
            pin.ShouldNotBeNull($"the PDK crossing must expose a port facing {angle}°");
            pin.LogicalPin.ShouldNotBeNull(
                $"the {angle}° port must be linked to a logical pin for S-matrix wiring");
        }
    }

    [Fact]
    public void RealPdkCrossing_ThroughLossReadFromSMatrix()
    {
        var crossing = CreateFromRealPdk();

        var lossDb = new CrossingInserter().GetCrossingThroughLossDb(crossing);

        lossDb.ShouldNotBeNull("the PDK S-matrix must yield a usable through-loss at 1550 nm");
        lossDb.Value.ShouldBe(ExpectedThroughLossDb, tolerance: 0.005);
    }

    [Fact]
    public void RealPdkCrossing_HasCrosstalkOnOrthogonalArms()
    {
        var crossing = CreateFromRealPdk();
        var west = CrossingPlacement.FindPinByAngle(crossing, 180)!;
        var north = CrossingPlacement.FindPinByAngle(crossing, 270)!;

        var sMatrix = crossing.WaveLengthToSMatrixMap[CrossingInserter.ReferenceWavelengthNm];
        var values = sMatrix.GetNonNullValues();

        values.TryGetValue(
                (west.LogicalPin!.IDInFlow, north.LogicalPin!.IDOutFlow), out var crosstalk)
            .ShouldBeTrue("the PDK S-matrix must model crosstalk onto the orthogonal arm");
        crosstalk.Magnitude.ShouldBe(0.02, tolerance: 0.001);
    }

    [Fact]
    public void InsertionPass_WithProductionPdkFactory_InsertsRealCrossing()
    {
        var layout = CrossingTestCircuit.Build(
            bendLossDbPer90Deg: 0.5, crossingFactory: CreateFromRealPdk);

        var crossing = layout.AddedCrossings.ShouldHaveSingleItem();
        crossing.NazcaFunctionName.ShouldBe(CrossingNazcaFunction);
        layout.Manager.Connections.Count.ShouldBe(4,
            "both nets must split into sub-connections docked at the real PDK crossing's ports");
        foreach (var sub in layout.Service.Records[0].AllSubConnections)
        {
            sub.IsPathValid.ShouldBeTrue();
            sub.IsBlockedFallback.ShouldBeFalse();
        }
    }
}
