using System.Numerics;
using CAP.Avalonia.Services;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Services;

/// <summary>
/// Tests for <see cref="PdkTemplateConverter.CreateSMatrixFromPdk"/>, focused on the
/// black-box default: a component draft without any S-matrix model (GDS import,
/// black-box custom component) must not swallow light. Exactly two optical pins get
/// the lossless bidirectional pass-through; every other topology stays absorbing
/// because no canonical model exists.
/// </summary>
public class PdkTemplateConverterTests
{
    private const double Tolerance = 1e-12;

    private static List<Pin> OpticalPins(params string[] names) =>
        names.Select((name, i) => new Pin(name, i, MatterType.Light, RectSide.Left)).ToList();

    [Fact]
    public void CreateSMatrixFromPdk_NullDraft_TwoOpticalPins_LosslessPassThroughBothDirections()
    {
        var pins = OpticalPins("in", "out");

        var matrix = PdkTemplateConverter.CreateSMatrixFromPdk(pins, null);

        var transfers = matrix.GetNonNullValues();
        transfers.Count.ShouldBe(2);
        transfers[(pins[0].IDInFlow, pins[1].IDOutFlow)].ShouldBe(Complex.One);
        transfers[(pins[1].IDInFlow, pins[0].IDOutFlow)].ShouldBe(Complex.One);
    }

    [Fact]
    public async Task CreateSMatrixFromPdk_NullDraft_TwoOpticalPins_InputLightArrivesAtOutputPin()
    {
        // The field iteration must carry unit amplitude through the component —
        // with an empty matrix the output pin sees zero field.
        var pins = OpticalPins("in", "out");
        var matrix = PdkTemplateConverter.CreateSMatrixFromPdk(pins, null);

        var input = MathNet.Numerics.LinearAlgebra.Vector<Complex>.Build.Dense(matrix.SMat.RowCount);
        input[matrix.PinReference[pins[0].IDInFlow]] = Complex.One;

        var field = await matrix.CalcFieldAtPinsAfterStepsAsync(
            input, maxIterations: 10, new CancellationTokenSource());

        field[pins[1].IDOutFlow].Magnitude.ShouldBe(1.0, Tolerance);
        field[pins[0].IDOutFlow].Magnitude.ShouldBe(0.0, Tolerance,
            "an ideal pass-through has no reflection");
    }

    [Fact]
    public void CreateSMatrixFromPdk_NullDraft_ThreeOpticalPins_StaysAbsorbing()
    {
        // No canonical model exists for a multi-port black box; an all-pairs
        // unit-magnitude default would be non-passive, so absorption is honest.
        var pins = OpticalPins("a", "b", "c");

        var matrix = PdkTemplateConverter.CreateSMatrixFromPdk(pins, null);

        matrix.GetNonNullValues().ShouldBeEmpty();
    }

    [Fact]
    public void CreateSMatrixFromPdk_NullDraft_SingleOpticalPin_StaysAbsorbing()
    {
        var pins = OpticalPins("only");

        var matrix = PdkTemplateConverter.CreateSMatrixFromPdk(pins, null);

        matrix.GetNonNullValues().ShouldBeEmpty();
    }

    [Fact]
    public void CreateSMatrixFromPdk_NullDraft_OneOpticalOneElectricalPin_StaysAbsorbing()
    {
        // An electrical pin carries no light; wiring it into the default would
        // invent an opto-electronic converter.
        var pins = new List<Pin>
        {
            new("o1", 0, MatterType.Light, RectSide.Left),
            new("e1", 1, MatterType.Electricity, RectSide.Right),
        };

        var matrix = PdkTemplateConverter.CreateSMatrixFromPdk(pins, null);

        matrix.GetNonNullValues().ShouldBeEmpty();
    }

    [Fact]
    public void CreateSMatrixFromPdk_NullDraft_TwoOpticalPinsPlusElectrical_PassesBetweenOpticalPins()
    {
        // e.g. an imported heated waveguide with a detected metal pad: the optical
        // path is still an unambiguous waveguide, the electrical pin is inert.
        var pins = new List<Pin>
        {
            new("in", 0, MatterType.Light, RectSide.Left),
            new("out", 1, MatterType.Light, RectSide.Right),
            new("heater", 2, MatterType.Electricity, RectSide.Up),
        };

        var matrix = PdkTemplateConverter.CreateSMatrixFromPdk(pins, null);

        var transfers = matrix.GetNonNullValues();
        transfers.Count.ShouldBe(2);
        transfers[(pins[0].IDInFlow, pins[1].IDOutFlow)].ShouldBe(Complex.One);
        transfers[(pins[1].IDInFlow, pins[0].IDOutFlow)].ShouldBe(Complex.One);
    }

    [Fact]
    public void CreateSMatrixFromPdk_ExplicitDraftWithoutConnections_StaysAbsorbing()
    {
        // Only a null draft means "no model"; a draft the author explicitly left
        // connection-less keeps the previous absorbing behavior.
        var pins = OpticalPins("in", "out");
        var draft = new PdkSMatrixDraft { WavelengthNm = 1550 };

        var matrix = PdkTemplateConverter.CreateSMatrixFromPdk(pins, draft);

        matrix.GetNonNullValues().ShouldBeEmpty();
    }

    [Fact]
    public void CreateSMatrixFromPdk_DraftWithConnections_KeepsAuthoredTransfers()
    {
        var pins = OpticalPins("in", "out");
        var draft = new PdkSMatrixDraft
        {
            WavelengthNm = 1550,
            Connections = new List<SMatrixConnection>
            {
                new() { FromPin = "in", ToPin = "out", Magnitude = 0.5, PhaseDegrees = 90 },
            },
        };

        var matrix = PdkTemplateConverter.CreateSMatrixFromPdk(pins, draft);

        var transfers = matrix.GetNonNullValues();
        transfers[(pins[0].IDInFlow, pins[1].IDOutFlow)].Magnitude.ShouldBe(0.5, Tolerance);
        transfers[(pins[1].IDInFlow, pins[0].IDOutFlow)].Magnitude.ShouldBe(0.5, Tolerance,
            "connections stay symmetrized as before");
    }
}
