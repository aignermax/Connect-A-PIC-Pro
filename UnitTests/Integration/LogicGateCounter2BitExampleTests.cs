using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Core;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Pinned tests for the shipped <c>examples/Logic Gate Counter 2-bit.lun</c> (issue
/// #1102, rung 5 of the NAND game — the datapath stone after the SR latch): a 2-bit
/// toggle (ripple) counter built from the shipped NOT/NAND gate plus 1×2 MMI splitter
/// copy gates. Stage 0 is the simplest toggle: the NAND register Q0 reads its own
/// committed output C0 back, so D0 = NAND(C0, S̄) = NOT(C0) once the active-low
/// preset toggle <c>S̄</c> (the network's one input, resting at 1) stays high —
/// the register <em>is</em> the inverter. Stage 1 is the textbook 4-NAND XOR whose
/// final NAND is the register Q1 itself: with X = NAND(C0, C1), R = NAND(C0, X),
/// S = NAND(C1, X), the committed next state is NAND(R, S) = C0 ⊕ C1, so C1 flips
/// exactly when C0 wraps from 1 back to 0. Because every register samples the same
/// settled pre-step state and commits simultaneously (D-semantics), each
/// <see cref="LogicNetworkEvaluator.Step"/> is one full clock tick, counting
/// C1C0 = 00 → 01 → 10 → 11 → 00. Every waveguide serves exactly one output pin and
/// one input pin (connecting a pin removes any older wire of it), so the committed
/// bits and the XOR pivot X fan out through the copy gates COPY0/COPY0X/COPY1/COPYX
/// — one waveguide carries exactly one signal, and each arm takes half the power,
/// comfortably above threshold. The file loads through the real load path, every
/// group carries its persisted <see cref="TruthTablePinAssignment"/> with the named
/// output taps <c>C0</c>, <c>C1</c>, <c>X</c>, <c>R</c> and <c>S</c>, and the merged
/// <see cref="LogicNetworkAssembler"/> (#988) turns the loaded design into the
/// evaluable network the sequence below pins.
/// </summary>
public class LogicGateCounter2BitExampleTests
    : IClassFixture<LogicGateCounter2BitExampleTests.CounterFixture>
{
    private const double NotThreshold = 0.375;
    private const double NandThreshold = 0.125;

    private const string LowBitTap = "C0";
    private const string HighBitTap = "C1";
    private const string PresetSignal = "S̄";

    private static readonly string[] GateNames =
        { "Q0", "COPY0", "COPY0X", "X0", "R0", "S0", "COPY1", "COPYX", "Q1" };
    private static readonly string[] RegisterNames = { "Q0", "Q1" };
    private static readonly string[] CopyNames = { "COPY0", "COPY0X", "COPY1", "COPYX" };

    /// <summary>The persisted output signal names per named gate group — the counter's named taps.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> ExpectedOutputSignalNames = new()
    {
        ["Q0"] = new() { ["Y"] = LowBitTap },
        ["X0"] = new() { ["Y"] = "X" },
        ["R0"] = new() { ["Y"] = "R" },
        ["S0"] = new() { ["Y"] = "S" },
        ["Q1"] = new() { ["Y"] = HighBitTap },
    };

    private readonly CounterFixture _fixture;

    /// <summary>Attaches the shared counter fixture.</summary>
    public LogicGateCounter2BitExampleTests(CounterFixture fixture) => _fixture = fixture;

    /// <summary>The resting input bits: the active-low preset toggle is never pulled while counting.</summary>
    private static Dictionary<string, bool> RestingInputBits() => new() { [PresetSignal] = true };

    [Fact]
    public void Example_LoadsOnlyTopLevelGateGroups_WithPersistedRolesAndRegisterDesignations()
    {
        _fixture.Canvas.Components.ShouldAllBe(
            c => c.Component is ComponentGroup,
            "the 2-bit counter contains only top-level gate groups");
        _fixture.Canvas.Connections.Count.ShouldBe(13,
            "thirteen wires fan the committed bits and the XOR pivot through the copy gates — " +
            "every waveguide keeps one driver and one load");

        var groups = _fixture.Groups;
        groups.Select(g => g.GroupName).ShouldBe(GateNames, ignoreOrder: true);
        foreach (var group in groups)
        {
            var roles = group.TruthTablePinAssignment.ShouldNotBeNull(
                $"group '{group.GroupName}' must ship its persisted pin roles");
            if (CopyNames.Contains(group.GroupName))
            {
                roles.InputPinNames.ShouldBe(new[] { "A" });
                roles.OutputPinNames.ShouldBe(new[] { "Y1", "Y2" });
                roles.BiasPinNames.ShouldBeEmpty();
                roles.Threshold.ShouldBe(NotThreshold);
                roles.IsRegister.ShouldBeFalse("a copy gate is combinational");
            }
            else
            {
                roles.InputPinNames.ShouldBe(new[] { "A", "B" });
                roles.OutputPinNames.ShouldBe(new[] { "Y" });
                roles.BiasPinNames.ShouldBe(new[] { "BIAS" });
                roles.Threshold.ShouldBe(NandThreshold);
                roles.IsRegister.ShouldBe(RegisterNames.Contains(group.GroupName),
                    $"the register designation of '{group.GroupName}' (issue #1098)");
            }
            if (ExpectedOutputSignalNames.TryGetValue(group.GroupName, out var namedOutputs))
            {
                roles.OutputSignalNames.ShouldBe(namedOutputs,
                    $"group '{group.GroupName}' ships its named output tap");
            }
            group.Description.ShouldContain("logic layer", Case.Sensitive,
                "every gate carries the education note about logic-layer composition");
        }

        _fixture.Groups.Single(g => g.GroupName == "Q0").TruthTablePinAssignment!
            .InputSignalNames.ShouldBe(new Dictionary<string, string> { ["B"] = PresetSignal },
                "Q0's second input ships the active-low preset signal name (issue #1025)");
        foreach (var register in RegisterNames)
        {
            _fixture.Groups.Single(g => g.GroupName == register).Description
                .ShouldContain("register", Case.Sensitive,
                    "every register carries the designation note");
        }
    }

    [Fact]
    public void AssembledNetwork_ExposesThePresetToggleNamedTapsAndTwoRegisters()
    {
        _fixture.Network.InputPinNames.ShouldBe(new[] { PresetSignal },
            customMessage: "Q0's preset pin stays unconnected as the network's only input S̄ — " +
                "a logic network must expose at least one input");
        _fixture.Network.OutputPinNames.ShouldBe(
            new[] { LowBitTap, HighBitTap, "X", "R", "S" }
                .Concat(CopyNames.SelectMany(name => new[] { $"{name}.Y1", $"{name}.Y2" })),
            ignoreOrder: true,
            customMessage: "five named taps replace the raw gate-pin names; the copy gates stay readable as raw taps");
        _fixture.Network.RegisterState.Keys.ShouldBe(
            new[] { new LogicPinRef("Q0", "Y"), new LogicPinRef("Q1", "Y") }, ignoreOrder: true,
            customMessage: "both stages power up as state elements, committed outputs cleared");
    }

    [Fact]
    public void StepSequence_CountsZeroToThreeAndWraps()
    {
        AssertCountsZeroToThreeAndWraps(_fixture.Network);
    }

    [Fact]
    public void StepSequence_ActiveLowPresetPulse_ForcesTheLowBitHigh()
    {
        var network = _fixture.Network;

        network.Evaluate(RestingInputBits());
        network.Step();
        network.Evaluate(RestingInputBits())[LowBitTap].ShouldBeTrue(
            "S̄ resting: stage 0 toggles — C1C0 = 01");

        network.Evaluate(RestingInputBits());
        network.Step();
        var wrapped = network.Evaluate(RestingInputBits());
        wrapped[HighBitTap].ShouldBeTrue();
        wrapped[LowBitTap].ShouldBeFalse("counting on: C1C0 = 10 after C0 wrapped");

        network.Evaluate(new Dictionary<string, bool> { [PresetSignal] = false });
        network.Step();
        network.Evaluate(RestingInputBits())[LowBitTap].ShouldBeTrue(
            "pulling S̄ to 0 for one step presets C0 to 1 regardless of the toggling");
    }

    /// <summary>
    /// Asserts the pinned counting sequence against one assembled network: power-up
    /// at C1C0 = 00, then 00 → 01 → 10 → 11 → 00 over four clock steps.
    /// </summary>
    private static void AssertCountsZeroToThreeAndWraps(LogicNetworkEvaluator network)
    {
        network.Evaluate(RestingInputBits())[HighBitTap].ShouldBeFalse();
        network.Evaluate(RestingInputBits())[LowBitTap].ShouldBeFalse(
            "powered up cleared: C1C0 = 00");

        foreach (var (expectedC1, expectedC0) in new[]
                 {
                     (false, true),   // 01 — stage 0 toggled
                     (true, false),   // 10 — C0 wrapped 1 → 0, stage 1 toggled
                     (true, true),    // 11
                     (false, false),  // 00 — both wrapped
                 })
        {
            network.Step();
            var settled = network.Evaluate(RestingInputBits());
            settled[HighBitTap].ShouldBe(expectedC1, "after the step C1C0 must read the next count");
            settled[LowBitTap].ShouldBe(expectedC0, "after the step C1C0 must read the next count");
            settled["X"].ShouldBe(!(expectedC0 && expectedC1), "the XOR pivot stays readable as a tap");
            settled["R"].ShouldBe(!(expectedC0 && !(expectedC0 && expectedC1)), "the C0 arm stays readable");
            settled["S"].ShouldBe(!(expectedC1 && !(expectedC0 && expectedC1)), "the C1 arm stays readable");
        }
    }

    [Fact]
    public async Task SaveLoadRoundTrip_ReAssembledNetwork_YieldsTheSameCounter()
    {
        var savedPath = await _fixture.SaveToTempFile();
        try
        {
            var reloadedCanvas = await LogicGateHalfAdderExampleTests.LoadCanvas(savedPath);

            var reloadedGroups = LogicGateHalfAdderExampleTests.GroupsOf(reloadedCanvas);
            reloadedGroups.Select(g => g.GroupName).ShouldBe(GateNames, ignoreOrder: true);
            reloadedGroups.ShouldAllBe(g => g.TruthTablePinAssignment != null,
                "the persisted pin roles must survive the save → load round trip");
            foreach (var group in reloadedGroups)
            {
                var roles = group.TruthTablePinAssignment!;
                roles.IsRegister.ShouldBe(RegisterNames.Contains(group.GroupName),
                    $"the register designation of '{group.GroupName}' must survive the save → load round trip");
                var expectedInputs = CopyNames.Contains(group.GroupName)
                    ? new[] { "A" } : new[] { "A", "B" };
                roles.InputPinNames.ToArray().ShouldBe(expectedInputs,
                    customMessage: $"the input roles of '{group.GroupName}' must survive the save → load round trip");
                if (ExpectedOutputSignalNames.TryGetValue(group.GroupName, out var namedOutputs))
                {
                    roles.OutputSignalNames.ShouldBe(namedOutputs,
                        $"the output signal names of '{group.GroupName}' must survive the save → load round trip");
                }
            }
            reloadedGroups.Single(g => g.GroupName == "Q0").TruthTablePinAssignment!.InputSignalNames
                .ShouldBe(new Dictionary<string, string> { ["B"] = PresetSignal },
                    "the preset signal name of Q0.B must survive the save → load round trip (#1025)");
            reloadedCanvas.Connections.Count.ShouldBe(13,
                "every counter wire must survive the save → load round trip");

            var reloaded = await LogicGateMuxExampleTests.AssembleNetwork(reloadedCanvas);
            reloaded.InputPinNames.ShouldBe(_fixture.Network.InputPinNames);
            reloaded.OutputPinNames.ShouldBe(_fixture.Network.OutputPinNames);
            reloaded.RegisterState.Keys.ShouldBe(_fixture.Network.RegisterState.Keys, ignoreOrder: true,
                customMessage: "the re-assembled network must carry the same register state elements");

            AssertCountsZeroToThreeAndWraps(reloaded);
        }
        finally
        {
            if (File.Exists(savedPath)) File.Delete(savedPath);
        }
    }

    /// <summary>
    /// Shared fixture: loads the shipped example once and assembles its logic network
    /// (each extraction is a real simulation run), so every fact asserts against the
    /// same loaded design and assembled network.
    /// </summary>
    public class CounterFixture : IAsyncLifetime
    {
        private const string ExampleFileName = "Logic Gate Counter 2-bit.lun";

        /// <summary>Laser wavelength the persisted roles were extracted at.</summary>
        public const int WavelengthNm = 1550;

        /// <summary>The canvas the shipped example loaded onto.</summary>
        public DesignCanvasViewModel Canvas { get; private set; } = null!;

        /// <summary>The loaded top-level gate groups, in file order.</summary>
        public List<ComponentGroup> Groups { get; private set; } = null!;

        /// <summary>The logic network assembled from the loaded design.</summary>
        public LogicNetworkEvaluator Network { get; private set; } = null!;

        /// <summary>Loads the shipped example and assembles its logic network.</summary>
        public async Task InitializeAsync()
        {
            var path = Path.Combine(ExampleDesignFilesTests.ExamplesDirectory(), ExampleFileName);
            Canvas = await LogicGateHalfAdderExampleTests.LoadCanvas(path);
            Groups = LogicGateHalfAdderExampleTests.GroupsOf(Canvas);
            Network = await LogicGateMuxExampleTests.AssembleNetwork(Canvas);
        }

        /// <summary>No shared state to release.</summary>
        public Task DisposeAsync() => Task.CompletedTask;

        /// <summary>Saves the loaded design through the real save path and returns the file path.</summary>
        public async Task<string> SaveToTempFile()
        {
            var path = Path.Combine(Path.GetTempPath(), $"counter-2bit-{Guid.NewGuid():N}.lun");
            var saveVm = LogicGateHalfAdderExampleTests.CreateFileOperations(Canvas);
            var dialog = new Mock<IFileDialogService>();
            dialog.Setup(f => f.ShowSaveFileDialogAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(path);
            saveVm.FileDialogService = dialog.Object;
            await saveVm.SaveDesignAsCommand.ExecuteAsync(null);
            File.Exists(path).ShouldBeTrue("the real save path must write the temp .lun");
            return path;
        }
    }
}
