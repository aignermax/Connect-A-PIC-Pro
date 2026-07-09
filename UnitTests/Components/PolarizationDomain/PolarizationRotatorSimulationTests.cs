using System.Numerics;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.ExternalPorts;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Components.PolarizationDomain;

/// <summary>
/// End-to-end acceptance test for issue #534: a polarization rotator
/// (declared with <see cref="PolarizationKind.Both"/> pins) bridges a TE
/// source to a TM output. The TE→rotator and rotator→TM connections are
/// polarization-legal, while a direct TE→TM connection is refused.
/// </summary>
public class PolarizationRotatorSimulationTests
{
    private const int WavelengthNm = 1550;
    private const double RotatorTransmission = 0.95;

    private static (Component component, PhysicalPin physicalPin, Pin logicalPin) CreateTerminal(
        string name, PolarizationKind polarization, double x, double efficiency)
    {
        var pin = new Pin("waveguide", 0, MatterType.Light, RectSide.Right)
        {
            Polarization = polarization
        };
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin> { pin });

        var sMatrix = new SMatrix(new List<Guid> { pin.IDInFlow, pin.IDOutFlow }, new());
        sMatrix.SetValues(new Dictionary<(Guid, Guid), Complex>
        {
            { (pin.IDInFlow, pin.IDOutFlow), new Complex(Math.Sqrt(efficiency), 0) }
        });

        var physicalPin = new PhysicalPin
        {
            Name = "waveguide",
            OffsetXMicrometers = 15,
            OffsetYMicrometers = 5,
            AngleDegrees = 0,
            LogicalPin = pin
        };

        var component = new Component(
            new Dictionary<int, SMatrix> { { WavelengthNm, sMatrix } },
            new List<Slider>(), "gc", "", parts, 0, name,
            DiscreteRotation.R0, new List<PhysicalPin> { physicalPin });
        component.PhysicalX = x;
        return (component, physicalPin, pin);
    }

    private static (Component component, PhysicalPin inPin, PhysicalPin outPin, Pin logicalOut) CreateRotator(double x)
    {
        // A polarization rotator accepts either polarization on both ports:
        // TE entering the input port leaves the output port as TM.
        var inPin = new Pin("in", 0, MatterType.Light, RectSide.Left) { Polarization = PolarizationKind.Both };
        var outPin = new Pin("out", 1, MatterType.Light, RectSide.Right) { Polarization = PolarizationKind.Both };
        var allPins = new List<Pin> { inPin, outPin };

        var parts = new Part[1, 1];
        parts[0, 0] = new Part(allPins);

        var pinIds = allPins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();
        var sMatrix = new SMatrix(pinIds, new());
        var amplitude = new Complex(Math.Sqrt(RotatorTransmission), 0);
        sMatrix.SetValues(new Dictionary<(Guid, Guid), Complex>
        {
            { (inPin.IDInFlow, outPin.IDOutFlow), amplitude },
            { (outPin.IDInFlow, inPin.IDOutFlow), amplitude }
        });

        var physicalPins = new List<PhysicalPin>
        {
            new() { Name = "in", OffsetXMicrometers = 0, OffsetYMicrometers = 5, AngleDegrees = 180, LogicalPin = inPin },
            new() { Name = "out", OffsetXMicrometers = 30, OffsetYMicrometers = 5, AngleDegrees = 0, LogicalPin = outPin }
        };

        var component = new Component(
            new Dictionary<int, SMatrix> { { WavelengthNm, sMatrix } },
            new List<Slider>(), "rotator", "", parts, 0, "PolarizationRotator_1",
            DiscreteRotation.R0, physicalPins);
        component.PhysicalX = x;
        return (component, physicalPins[0], physicalPins[1], outPin);
    }

    [Fact]
    public void DirectTeToTmConnection_IsRefusedByRules()
    {
        var (_, tePin, _) = CreateTerminal("GC_TE", PolarizationKind.TE, 0, 0.3);
        var (_, tmPin, _) = CreateTerminal("GC_TM", PolarizationKind.TM, 100, 0.3);

        PolarizationRules.CanConnect(tePin.Polarization, tmPin.Polarization).ShouldBeFalse();
    }

    [Fact]
    public void RotatorPins_AcceptBothTeAndTmNeighbours()
    {
        var (_, tePin, _) = CreateTerminal("GC_TE", PolarizationKind.TE, 0, 0.3);
        var (_, tmPin, _) = CreateTerminal("GC_TM", PolarizationKind.TM, 150, 0.3);
        var (_, rotatorIn, rotatorOut, _) = CreateRotator(50);

        PolarizationRules.CanConnect(tePin.Polarization, rotatorIn.Polarization).ShouldBeTrue();
        PolarizationRules.CanConnect(rotatorOut.Polarization, tmPin.Polarization).ShouldBeTrue();
    }

    [Fact]
    public async Task TeInput_ThroughRotator_ReachesTmOutputWithExpectedTransmission()
    {
        // GC_TE (source) → Rotator (Both/Both) → GC_TM (detector)
        var source = CreateTerminal("GC_TE_In", PolarizationKind.TE, 0, 1.0);
        var rotator = CreateRotator(50);
        var detector = CreateTerminal("GC_TM_Out", PolarizationKind.TM, 150, 1.0);

        var tileManager = new ComponentListTileManager();
        tileManager.AddComponent(source.component);
        tileManager.AddComponent(rotator.component);
        tileManager.AddComponent(detector.component);

        var connectionManager = new WaveguideConnectionManager(new WaveguideRouter());
        connectionManager.AddExistingConnection(new WaveguideConnection
        {
            StartPin = source.physicalPin,
            EndPin = rotator.inPin
        });
        connectionManager.AddExistingConnection(new WaveguideConnection
        {
            StartPin = rotator.outPin,
            EndPin = detector.physicalPin
        });

        var portManager = new PhysicalExternalPortManager();
        var lightSource = new ExternalInput("src_te", LaserType.Red, 0, Complex.One);
        portManager.AddLightSource(lightSource, source.logicalPin.IDInFlow);

        var gridManager = GridManager.CreateForSimulation(tileManager, connectionManager, portManager);
        var builder = new SystemMatrixBuilder(gridManager);
        var calculator = new GridLightCalculator(builder, gridManager);

        var fieldResults = await calculator.CalculateFieldPropagationAsync(
            new CancellationTokenSource(), WavelengthNm);

        // TE light must arrive at the TM detector input through the rotator,
        // attenuated exactly by the rotator's transmission amplitude
        // (sqrt(0.95)) relative to the source output. Comparing the ratio
        // keeps the assertion independent of the calculator's absolute
        // normalization of field amplitudes.
        var amplitudeAtSourceOutput = fieldResults[source.logicalPin.IDOutFlow].Magnitude;
        var amplitudeAtTmDetector = fieldResults[detector.logicalPin.IDInFlow].Magnitude;
        amplitudeAtSourceOutput.ShouldBeGreaterThan(0, "No light left the TE source");
        (amplitudeAtTmDetector / amplitudeAtSourceOutput)
            .ShouldBe(Math.Sqrt(RotatorTransmission), tolerance: 1e-6);

        // And the light passing the rotator's output pin is TM-compatible:
        // the detector pin it feeds is declared TM.
        detector.physicalPin.Polarization.ShouldBe(PolarizationKind.TM);
        rotator.outPin.Polarization.ShouldBe(PolarizationKind.Both);
    }
}
