using CAP.Avalonia.Services;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Services;

/// <summary>
/// Tests for <see cref="PdkTemplateConverter.CreateSMatrixFromPdk"/> (issue
/// #1005): a draft without a simulation model (<c>null</c> S-matrix — GDS
/// imports, black-box custom components) must default to a lossless
/// pass-through on 2-optical-pin components instead of absorbing all light.
/// </summary>
public class PdkTemplateConverterTests
{
    private static List<Pin> OpticalPins(params string[] names) =>
        names.Select((name, i) => new Pin(name, i, MatterType.Light, RectSide.Left)).ToList();

    [Fact]
    public void NullSMatrixDraft_TwoOpticalPins_CreatesBidirectionalLosslessPassThrough()
    {
        var pins = OpticalPins("in", "out");

        var sMatrix = PdkTemplateConverter.CreateSMatrixFromPdk(pins, null);

        var transfers = sMatrix.GetNonNullValues();
        transfers.Count.ShouldBe(2);
        transfers[(pins[0].IDInFlow, pins[1].IDOutFlow)].Magnitude.ShouldBe(1.0, 1e-12);
        transfers[(pins[1].IDInFlow, pins[0].IDOutFlow)].Magnitude.ShouldBe(1.0, 1e-12);
    }

    [Fact]
    public void NullSMatrixDraft_TwoOpticalPins_LightActuallyPropagatesThroughTheMatrix()
    {
        var pins = OpticalPins("in", "out");
        var sMatrix = PdkTemplateConverter.CreateSMatrixFromPdk(pins, null);

        var systemMatrix = SMatrix.CreateSystemSMatrix(new List<SMatrix> { sMatrix });
        var input = MathNet.Numerics.LinearAlgebra.Vector<System.Numerics.Complex>.Build
            .Sparse(systemMatrix.SMat.RowCount);
        input[systemMatrix.PinReference[pins[0].IDInFlow]] = System.Numerics.Complex.One;

        var field = systemMatrix.CalcFieldAtPinsAfterStepsAsync(
            input, maxIterations: 10, new CancellationTokenSource()).Result;

        field[pins[1].IDOutFlow].Magnitude.ShouldBeGreaterThan(0,
            "light entering pin 'in' must exit at pin 'out' (lossless pass-through)");
        field[pins[1].IDOutFlow].Magnitude.ShouldBe(1.0, 1e-9);
    }

    [Fact]
    public void NullSMatrixDraft_SingleOpticalPin_StaysAbsorbing()
    {
        var pins = OpticalPins("in");

        var sMatrix = PdkTemplateConverter.CreateSMatrixFromPdk(pins, null);

        sMatrix.GetNonNullValues().ShouldBeEmpty(
            "a 1-pin component (probe/bond pad) has no pass-through partner");
    }

    [Fact]
    public void NullSMatrixDraft_ThreeOpticalPins_StaysAbsorbing()
    {
        var pins = OpticalPins("in", "out1", "out2");

        var sMatrix = PdkTemplateConverter.CreateSMatrixFromPdk(pins, null);

        sMatrix.GetNonNullValues().ShouldBeEmpty(
            "a multi-port default would have to guess the routing and violate passivity");
    }

    [Fact]
    public void NullSMatrixDraft_OneOpticalPlusOneElectricalPin_StaysAbsorbing()
    {
        var pins = OpticalPins("in");
        pins.Add(new Pin("vcc", 1, MatterType.Electricity, RectSide.Right));

        var sMatrix = PdkTemplateConverter.CreateSMatrixFromPdk(pins, null);

        sMatrix.GetNonNullValues().ShouldBeEmpty(
            "an electrical pin is never a light pass-through partner");
    }

    [Fact]
    public void ExplicitEmptyConnections_TwoOpticalPins_StaysAbsorbing()
    {
        var pins = OpticalPins("in", "out");
        var draft = new PdkSMatrixDraft { WavelengthNm = 1550, Connections = new List<SMatrixConnection>() };

        var sMatrix = PdkTemplateConverter.CreateSMatrixFromPdk(pins, draft);

        sMatrix.GetNonNullValues().ShouldBeEmpty(
            "an explicitly declared empty connection list is honored as an intentional absorber");
    }

    [Fact]
    public void DeclaredConnections_TwoOpticalPins_AreUnaffectedByTheDefault()
    {
        var pins = OpticalPins("in", "out");
        var draft = new PdkSMatrixDraft
        {
            WavelengthNm = 1550,
            Connections = new List<SMatrixConnection>
            {
                new() { FromPin = "in", ToPin = "out", Magnitude = 0.5, PhaseDegrees = 0 },
            },
        };

        var sMatrix = PdkTemplateConverter.CreateSMatrixFromPdk(pins, draft);

        var transfers = sMatrix.GetNonNullValues();
        transfers.Count.ShouldBe(2);
        transfers[(pins[0].IDInFlow, pins[1].IDOutFlow)].Magnitude.ShouldBe(0.5, 1e-12);
    }
}
