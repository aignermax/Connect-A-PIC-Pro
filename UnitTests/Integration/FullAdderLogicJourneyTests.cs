using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Hard end-to-end scenario of the whole rung-4 logic chain as ONE journey (issue
/// #1022): the shipped <c>examples/Logic Gate Full Adder.lun</c> walked through load →
/// assemble → truth table → timing → fan-out levels → save/load repeat. Every station
/// has its own suite elsewhere (#984 roles, #983 builder, #988 assembler, #1002/#1009
/// delays, #996/#1011 fan-out); this class is the proof the whole road walks as one
/// continuous journey over one shared fixture — drift between the features fails here
/// even when every individual suite stays green.
///
///   Step 1: the .lun loads — 32 gate groups carry persisted roles + thresholds.
///   Step 2: <see cref="LogicNetworkAssembler"/> builds the network — every gate model
///           is the optically re-simulated truth table at the persisted roles.
///   Step 3: all 8 input combinations yield the full-adder Sum/Cout.
///   Step 4: every gate delay &gt; 0; the critical path equals an independent
///           recomputation from the exposed per-gate delays over the design's DAG.
///   Step 5: both addend signal fan-out sites carry quantitative level reports whose
///           verdicts match the #1018-documented expectation (both fail physically).
///   Step 6: save → load → repeat — the round-tripped design reproduces steps 2–5
///           identically (table, delays within 1e-9, same verdicts).
/// </summary>
public class FullAdderLogicJourneyTests
    : IClassFixture<LogicGateFullAdderExampleTests.FullAdderFixture>
{
    private const double DelayTolerance = 1e-9;
    private const double PowerTolerance = 1e-12;
    private const double FullInputPower = 1.0;
    private const double NandThreshold = 0.125;
    private const int GateCount = 32;
    private const int WireCount = 30;
    private const int NetworkInputCount = 3;
    private const int LoadsOfSignalA = 13;
    private const int LoadsOfSignalB = 13;

    private readonly LogicGateFullAdderExampleTests.FullAdderFixture _journey;

    /// <summary>Attaches the shared journey fixture.</summary>
    public FullAdderLogicJourneyTests(LogicGateFullAdderExampleTests.FullAdderFixture journey) =>
        _journey = journey;

    [Fact]
    public void Step1_Load_ExampleArrivesAs32GateGroups_WithPersistedRolesAndThresholds()
    {
        _journey.Groups.Count.ShouldBe(GateCount,
            "Step 1: the shipped full adder is thirty-two gate groups");
        _journey.Groups.ShouldAllBe(g => g.TruthTablePinAssignment != null,
            "Step 1: every gate group must carry its persisted pin roles");
        _journey.Groups.ShouldAllBe(g => g.TruthTablePinAssignment!.Threshold > 0,
            "Step 1: every gate group must carry its persisted power threshold");
        _journey.Canvas.Connections.Count.ShouldBe(WireCount,
            "Step 1: thirty wires join the thirty-two gates");
    }

    [Fact]
    public void Step2_Assemble_NetworkExposesOperandsAndTaps_AsResimulatedTables()
    {
        var network = _journey.Network;
        network.Gates.Count.ShouldBe(GateCount,
            "Step 2: every gate group becomes a network gate");
        network.InputPinNames.Count.ShouldBe(NetworkInputCount,
            "Step 2: the persisted signal names merge the operand pins into A, B, Cin (#1025)");
        network.OutputPinNames.Count.ShouldBe(GateCount,
            "Step 2: every gate output is a network-level tap");
        network.Gates.Values.ShouldAllBe(
            g => g.TruthTable.Rows.Count == (1 << g.InputPinNames.Count),
            "Step 2: every gate model is the optically re-simulated truth table at its persisted roles");
    }

    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(false, true, false, true, false)]
    [InlineData(true, true, false, false, true)]
    [InlineData(false, false, true, true, false)]
    [InlineData(true, false, true, false, true)]
    [InlineData(false, true, true, false, true)]
    [InlineData(true, true, true, true, true)]
    public void Step3_TruthTable_AllInputCombinations_YieldFullAdderSumAndCarryOut(
        bool a, bool b, bool cin, bool expectedSum, bool expectedCout)
    {
        var result = _journey.Network.Evaluate(_journey.InputBits(a, b, cin));

        result["H2SUM.Y"].ShouldBe(expectedSum,
            $"Step 3: Sum = A⊕B⊕Cin for A={a}, B={b}, Cin={cin}");
        result["OROUT.Y"].ShouldBe(expectedCout,
            $"Step 3: Cout = majority(A, B, Cin) for A={a}, B={b}, Cin={cin}");
    }

    [Fact]
    public void Step4_Timing_EveryGateDelayPositive_CriticalPathMatchesIndependentRecomputation()
    {
        var network = _journey.Network;
        network.GateDelaysPicoseconds.Values.ShouldAllBe(
            delay => delay > 0 && double.IsFinite(delay),
            "Step 4: every gate has a non-zero, finite propagation delay");

        network.CriticalPathDelayPicoseconds.ShouldBe(RecomputeCriticalPath(network), DelayTolerance,
            "Step 4: the critical path equals the max cumulative delay recomputed from the " +
            "exposed per-gate delays over the design's DAG");
        network.CriticalPathGateIds.Count.ShouldBeGreaterThan(1,
            "Step 4: the critical path is a chain of gates, not a single gate");
        network.CriticalPathGateIds.Sum(id => network.GateDelaysPicoseconds[id])
            .ShouldBe(network.CriticalPathDelayPicoseconds, DelayTolerance,
                "Step 4: the critical path is the sum of the delays along its gate chain");
    }

    [Theory]
    [InlineData("A", LoadsOfSignalA)]
    [InlineData("B", LoadsOfSignalB)]
    public void Step5_FanOutLevels_BothMergedAddendSites_FailPhysicallyAsDocumented(
        string signal, int expectedLoads)
    {
        var warning = _journey.Network.FanOutWarnings
            .SingleOrDefault(w => w.DriverDisplayName == signal)
            .ShouldNotBeNull($"Step 5: the merged addend site '{signal}' must carry a fan-out report");

        warning.IsNetworkInputSignal.ShouldBeTrue(
            $"Step 5: site '{signal}' is one shared network-input source");
        warning.LoadCount.ShouldBe(expectedLoads);
        warning.Levels.DriverPowerOne.ShouldBe(FullInputPower,
            $"Step 5: site '{signal}' is driven at the full input power");
        warning.Levels.BranchPower.ShouldBe(FullInputPower / expectedLoads, PowerTolerance,
            $"Step 5: BranchPower = DriverPower/N = 1/{expectedLoads}");
        warning.Levels.Branches.Count.ShouldBe(expectedLoads,
            $"Step 5: site '{signal}' carries one verdict per receiving input");
        warning.Levels.Branches.ShouldAllBe(b => !b.ReadsAsOne && b.Threshold == NandThreshold,
            $"Step 5: 1/{expectedLoads} ≈ {FullInputPower / expectedLoads:0.###} < {NandThreshold} — " +
            "the ideally split signal can no longer switch any receiving NAND gate (#1018's honest lesson)");
    }

    [Fact]
    public async Task Step6_SaveLoadRepeat_RoundTrippedDesign_ReproducesTheWholeJourney()
    {
        var savedPath = await _journey.SaveToTempFile();
        try
        {
            var reloadedCanvas = await LogicGateHalfAdderExampleTests.LoadCanvas(savedPath);
            var reloaded = await LogicGateFullAdderExampleTests.AssembleNetwork(reloadedCanvas);

            reloaded.InputPinNames.ShouldBe(_journey.Network.InputPinNames,
                "Step 6: the re-assembled network exposes the same operands");
            reloaded.OutputPinNames.ShouldBe(_journey.Network.OutputPinNames,
                "Step 6: the re-assembled network exposes the same taps");

            foreach (var a in new[] { false, true })
            foreach (var b in new[] { false, true })
            foreach (var cin in new[] { false, true })
            {
                reloaded.Evaluate(_journey.InputBits(a, b, cin))
                    .ShouldBe(_journey.Network.Evaluate(_journey.InputBits(a, b, cin)),
                        $"Step 6: identical truth table for A={a}, B={b}, Cin={cin}");
            }

            foreach (var (gateId, delay) in _journey.Network.GateDelaysPicoseconds)
            {
                reloaded.GateDelaysPicoseconds[gateId].ShouldBe(delay, DelayTolerance,
                    $"Step 6: gate '{gateId}' keeps its propagation delay");
            }
            reloaded.CriticalPathDelayPicoseconds.ShouldBe(
                _journey.Network.CriticalPathDelayPicoseconds, DelayTolerance,
                "Step 6: the critical path delay is identical after the round-trip");

            var expectedSites = _journey.Network.FanOutWarnings.OrderBy(w => w.DriverDisplayName).ToList();
            var actualSites = reloaded.FanOutWarnings.OrderBy(w => w.DriverDisplayName).ToList();
            actualSites.Select(w => w.DriverDisplayName)
                .ShouldBe(expectedSites.Select(w => w.DriverDisplayName),
                    "Step 6: the same fan-out sites warn after the round-trip");
            foreach (var (expected, actual) in expectedSites.Zip(actualSites))
            {
                actual.LoadCount.ShouldBe(expected.LoadCount);
                actual.Levels.DriverPowerOne.ShouldBe(expected.Levels.DriverPowerOne);
                actual.Levels.BranchPower.ShouldBe(expected.Levels.BranchPower);
                actual.Levels.Branches.ShouldBe(expected.Levels.Branches,
                    $"Step 6: site '{expected.DriverDisplayName}' keeps its verdicts");
            }
        }
        finally
        {
            if (File.Exists(savedPath)) File.Delete(savedPath);
        }
    }

    /// <summary>
    /// Independently recomputes the critical-path delay from the network's exposed
    /// per-gate delays over the design's own gate DAG — derived from the canvas
    /// connections and the persisted pin roles, never from the network's private
    /// wiring: cumulative delay per gate in topological order, then the maximum over
    /// the tapped gates.
    /// </summary>
    private double RecomputeCriticalPath(LogicNetworkEvaluator network)
    {
        var drivers = GateDriversByLoad();
        var cumulative = new Dictionary<string, double>();
        var remaining = drivers.Keys.ToList();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(id => drivers[id].All(cumulative.ContainsKey)).ToList();
            ready.ShouldNotBeEmpty("Step 4: the design's gate graph must be a DAG");
            foreach (var id in ready)
            {
                cumulative[id] = network.GateDelaysPicoseconds[id]
                    + drivers[id].Select(d => cumulative[d]).DefaultIfEmpty(0).Max();
                remaining.Remove(id);
            }
        }
        return network.OutputTaps.Values.Select(pin => cumulative[pin.GateId]).Max();
    }

    /// <summary>The driver gates of every gate, derived from the canvas wiring and the persisted roles.</summary>
    private IReadOnlyDictionary<string, List<string>> GateDriversByLoad()
    {
        var drivers = _journey.Groups.ToDictionary(g => g.GroupName, _ => new List<string>());
        foreach (var connection in _journey.Canvas.Connections.Select(c => c.Connection))
        {
            var start = ResolveGatePin(connection.StartPin);
            var end = ResolveGatePin(connection.EndPin);
            if (start == null || end == null) continue;
            var (driver, load) = start.Value.IsOutput
                ? (start.Value.GateId, end.Value.GateId)
                : (end.Value.GateId, start.Value.GateId);
            if (!drivers[load].Contains(driver))
                drivers[load].Add(driver);
        }
        return drivers;
    }

    /// <summary>Resolves one wire endpoint to its gate id and whether it is the gate's output pin.</summary>
    private (string GateId, bool IsOutput)? ResolveGatePin(PhysicalPin? pin)
    {
        if (pin == null) return null;
        foreach (var group in _journey.Groups)
        {
            var roles = group.TruthTablePinAssignment;
            if (roles == null) continue;
            var pinName = ReferenceEquals(pin.ParentComponent, group)
                ? pin.Name
                : group.ExternalPins.FirstOrDefault(p => ReferenceEquals(p.InternalPin, pin))?.Name;
            if (pinName == null) continue;
            if (roles.OutputPinNames.Contains(pinName)) return (group.GroupName, true);
            if (roles.InputPinNames.Contains(pinName)) return (group.GroupName, false);
        }
        return null;
    }
}
