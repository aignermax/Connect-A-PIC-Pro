using System.Numerics;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.ExternalPorts;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using CAP_Core.LightCalculation.PowerFlow;
using Shouldly;
using Xunit;

namespace UnitTests.Simulation;

/// <summary>
/// End-to-end integration tests verifying light propagation through a
/// multi-component circuit: GC → Splitter → 2x DC → 4x GC outputs.
/// </summary>
[Trait("Category", "Slow")]
public class SimulationIntegrationTests
{
    private static readonly int[] AllWavelengths =
    {
        StandardWaveLengths.RedNM,
        StandardWaveLengths.GreenNM,
        StandardWaveLengths.BlueNM
    };

    /// <summary>
    /// Verifies that light propagates from a single input grating coupler
    /// through a splitter and two directional couplers to four output GCs.
    /// </summary>
    [Fact]
    public async Task LightPropagatesThroughFullCircuit()
    {
        var circuit = IntegrationCircuitBuilder.BuildSplitterToDualCouplerCircuit(AllWavelengths);
        var (gridManager, portManager) = SetupSimulation(circuit, LaserType.Red);

        var builder = new SystemMatrixBuilder(gridManager);
        var calculator = new GridLightCalculator(builder, gridManager);
        var cts = new CancellationTokenSource();
        var fields = await calculator.CalculateFieldPropagationAsync(cts, StandardWaveLengths.RedNM);

        // Light should reach the input GC output
        var gcInputPin = circuit.GcInput.LogicalPins[0];
        fields[gcInputPin.IDOutFlow].Magnitude.ShouldBeGreaterThan(0, "No light at GC input output");

        // Light should reach both splitter outputs
        var splitterOut1 = circuit.Splitter.LogicalPins[1];
        var splitterOut2 = circuit.Splitter.LogicalPins[2];
        fields[splitterOut1.IDOutFlow].Magnitude.ShouldBeGreaterThan(0, "No light at splitter out1");
        fields[splitterOut2.IDOutFlow].Magnitude.ShouldBeGreaterThan(0, "No light at splitter out2");

        // Light should reach all four output GCs
        for (int i = 0; i < circuit.GcOutputs.Length; i++)
        {
            var outputPin = circuit.GcOutputs[i].LogicalPins[0];
            fields[outputPin.IDInFlow].Magnitude
                .ShouldBeGreaterThan(0, $"No light at output GC {i + 1}");
        }
    }

    /// <summary>
    /// Verifies 50/50 splitting ratio at the splitter: both output branches
    /// should carry approximately equal power.
    /// </summary>
    [Fact]
    public async Task SplitterProducesEqualPowerSplit()
    {
        var circuit = IntegrationCircuitBuilder.BuildSplitterToDualCouplerCircuit(AllWavelengths);
        var (gridManager, _) = SetupSimulation(circuit, LaserType.Red);

        var builder = new SystemMatrixBuilder(gridManager);
        var calculator = new GridLightCalculator(builder, gridManager);
        var cts = new CancellationTokenSource();
        var fields = await calculator.CalculateFieldPropagationAsync(cts, StandardWaveLengths.RedNM);

        var splitterOut1 = circuit.Splitter.LogicalPins[1];
        var splitterOut2 = circuit.Splitter.LogicalPins[2];

        var power1 = fields[splitterOut1.IDOutFlow].Magnitude;
        var power2 = fields[splitterOut2.IDOutFlow].Magnitude;

        power1.ShouldBeGreaterThan(0, "Splitter out1 has zero power");
        power2.ShouldBeGreaterThan(0, "Splitter out2 has zero power");

        var ratio = power1 / power2;
        ratio.ShouldBeInRange(0.95, 1.05, "Splitter ratio deviates from 50/50");
    }

    /// <summary>
    /// Verifies power conservation: total output power must not exceed input power.
    /// </summary>
    [Fact]
    public async Task TotalOutputPowerDoesNotExceedInput()
    {
        var circuit = IntegrationCircuitBuilder.BuildSplitterToDualCouplerCircuit(AllWavelengths);
        var (gridManager, _) = SetupSimulation(circuit, LaserType.Red);

        var builder = new SystemMatrixBuilder(gridManager);
        var calculator = new GridLightCalculator(builder, gridManager);
        var cts = new CancellationTokenSource();
        var fields = await calculator.CalculateFieldPropagationAsync(cts, StandardWaveLengths.RedNM);

        double inputPower = 1.0; // Injected power
        double totalOutputPower = 0;
        foreach (var gcOut in circuit.GcOutputs)
        {
            var pin = gcOut.LogicalPins[0];
            var amplitude = fields[pin.IDOutFlow].Magnitude;
            totalOutputPower += amplitude * amplitude;
        }

        totalOutputPower.ShouldBeLessThanOrEqualTo(
            inputPower, "Total output power exceeds input — conservation violated");
    }

    /// <summary>
    /// Verifies that simulation produces non-zero results at all wavelengths.
    /// </summary>
    [Theory]
    [InlineData(1550)]
    [InlineData(1310)]
    [InlineData(980)]
    public async Task SimulationWorksAtMultipleWavelengths(int wavelengthNm)
    {
        var circuit = IntegrationCircuitBuilder.BuildSplitterToDualCouplerCircuit(AllWavelengths);
        var laserType = GetLaserTypeForWavelength(wavelengthNm);
        var (gridManager, _) = SetupSimulation(circuit, laserType);

        var builder = new SystemMatrixBuilder(gridManager);
        var calculator = new GridLightCalculator(builder, gridManager);
        var cts = new CancellationTokenSource();
        var fields = await calculator.CalculateFieldPropagationAsync(cts, wavelengthNm);

        foreach (var gcOut in circuit.GcOutputs)
        {
            var pin = gcOut.LogicalPins[0];
            fields[pin.IDInFlow].Magnitude
                .ShouldBeGreaterThan(0, $"No light at output at {wavelengthNm}nm");
        }
    }

    /// <summary>
    /// Verifies PowerFlowAnalyzer reports non-zero power on all connections.
    /// </summary>
    [Fact]
    public async Task PowerFlowAnalyzerReportsAllConnectionsActive()
    {
        var circuit = IntegrationCircuitBuilder.BuildSplitterToDualCouplerCircuit(AllWavelengths);
        var (gridManager, _) = SetupSimulation(circuit, LaserType.Red);

        var builder = new SystemMatrixBuilder(gridManager);
        var calculator = new GridLightCalculator(builder, gridManager);
        var cts = new CancellationTokenSource();
        var fields = await calculator.CalculateFieldPropagationAsync(cts, StandardWaveLengths.RedNM);

        var analyzer = new PowerFlowAnalyzer();
        var result = analyzer.Analyze(circuit.ConnectionManager.Connections, fields);

        result.ConnectionFlows.Count.ShouldBe(
            circuit.ConnectionManager.Connections.Count,
            "Not all connections have power flow data");

        foreach (var flow in result.ConnectionFlows.Values)
        {
            flow.AveragePower.ShouldBeGreaterThan(0, "Connection has zero power");
            flow.NormalizedPowerFraction.ShouldBeGreaterThan(0, "Normalized fraction is zero");
        }

        result.MaxPower.ShouldBeGreaterThan(0, "Max power is zero");
    }

    private static (GridManager, PhysicalExternalPortManager) SetupSimulation(
        CircuitSetup circuit, LaserType laserType)
    {
        var portManager = new PhysicalExternalPortManager();
        var inputPin = circuit.GcInput.LogicalPins[0];
        var lightSource = new ExternalInput("laser", laserType, 0, new Complex(1.0, 0));
        portManager.AddLightSource(lightSource, inputPin.IDInFlow);

        var gridManager = GridManager.CreateForSimulation(
            circuit.TileManager, circuit.ConnectionManager, portManager);

        return (gridManager, portManager);
    }

    private static LaserType GetLaserTypeForWavelength(int wavelengthNm)
    {
        return wavelengthNm switch
        {
            1550 => LaserType.Red,
            1310 => LaserType.Green,
            980 => LaserType.Blue,
            _ => LaserType.Red
        };
    }
}
