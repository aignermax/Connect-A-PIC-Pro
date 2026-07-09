using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Components.FormulaReading;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.MetalRouting;

/// <summary>
/// Tests <see cref="WaveguideConnection.IsElectrical"/> and the demo PDK's
/// electrical probe pad / metal process data (issue #682).
/// </summary>
public class ElectricalConnectionTests
{
    [Fact]
    public void IsElectrical_BothPinsElectrical_IsTrue()
    {
        var conn = new WaveguideConnection
        {
            StartPin = CreatePin(MatterType.Electricity),
            EndPin = CreatePin(MatterType.Electricity),
        };

        conn.IsElectrical.ShouldBeTrue();
    }

    [Fact]
    public void IsElectrical_OpticalPins_IsFalse()
    {
        var conn = new WaveguideConnection
        {
            StartPin = CreatePin(MatterType.Light),
            EndPin = CreatePin(MatterType.Light),
        };

        conn.IsElectrical.ShouldBeFalse();
    }

    [Fact]
    public void IsElectrical_PinWithoutLogicalPin_DefaultsToOptical()
    {
        var component = CreateComponent();
        var pin = new PhysicalPin { Name = "p", ParentComponent = component };
        var conn = new WaveguideConnection { StartPin = pin, EndPin = pin };

        conn.IsElectrical.ShouldBeFalse();
    }

    [Fact]
    public void DemoPdk_ContainsProbePadWithElectricalPin_AndMetalXsection()
    {
        var demoPdkPath = Path.Combine(
            Directory.GetCurrentDirectory(), "..", "..", "..", "..",
            "CAP-DataAccess", "PDKs", "demo-pdk.json");
        if (!File.Exists(demoPdkPath))
            return; // Skip when the PDK is not shipped with the test run.

        var pdk = new PdkLoader().LoadFromFile(demoPdkPath);

        var probePad = pdk.Components.FirstOrDefault(c => c.Name == "Probe Pad");
        probePad.ShouldNotBeNull();
        probePad.Pins.ShouldContain(p => p.PinKind == "Electrical");

        pdk.Process.ShouldNotBeNull();
        var metal = pdk.Process.Xsections.FirstOrDefault(
            x => x.Kind == CAP_DataAccess.Components.ComponentDraftMapper.DTOs.XsectionKind.Metal);
        metal.ShouldNotBeNull();
        metal.WidthUm.ShouldBeGreaterThan(0);
    }

    private static PhysicalPin CreatePin(MatterType matterType)
    {
        var component = CreateComponent();
        return new PhysicalPin
        {
            Name = "p",
            ParentComponent = component,
            LogicalPin = new Pin("p", 0, matterType, RectSide.Right),
        };
    }

    private static Component CreateComponent()
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        return new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: "test",
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: "test",
            rotationCounterClock: DiscreteRotation.R0);
    }
}
