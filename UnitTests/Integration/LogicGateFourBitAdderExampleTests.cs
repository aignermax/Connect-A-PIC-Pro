using System.Diagnostics;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Core;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Pinned tests for the shipped <c>examples/Logic Gate 4-Bit Adder.lun</c> (issue #1023,
/// rung 4→5 of the NAND game): 344 top-level instances of the NOT/NAND gate (256 NAND,
/// 88 NOT) — four full-adder stages reusing the shipped full adder's structure (#990),
/// carry rippling stage to stage (Cout_i → Cin_i+1). The canvas wires one waveguide per
/// pin, so a stage's carry-out — consumed by the next stage on 3 + K carry-in pins (the
/// sum XOR ladder's three plus one per duplicated carry stage) — is duplicated with its
/// carry-OR subtree per consumer: K = (10, 7, 4, 1) with K_i = 3 + K_i+1; stage 3 taps
/// Cout directly. The fan-out of the operand bits happens at the logic layer, where the
/// persisted signal names (issues #1025/#1034) merge the 261 unconnected operand pins
/// into exactly nine network inputs — A0–A3, B0–B3 and Cin, the nine toggles the Logic
/// panel shows. Outputs carry signal names, too (#1046): the sum taps read S0–S3
/// (<c>T{i}H2SUM.Y</c>) and the carry-out Cout (<c>T3OROUT.Y</c>),
/// checked against the arithmetic sum. The fixture records the assembler wall clock
/// for the PR's scale report.
/// </summary>
public class LogicGateFourBitAdderExampleTests : IClassFixture<LogicGateFourBitAdderExampleTests.FourBitAdderFixture>
{
    private const double NandThreshold = 0.125;
    private const double NotThreshold = 0.375;

    /// <summary>Carry-out copies per stage: K_i = 3 + K_i+1; the last stage taps Cout directly.</summary>
    private static readonly int[] CarryCopies = { 10, 7, 4, 1 };

    /// <summary>The nine network inputs the operand pins merge into: four A bits, four B bits, Cin.</summary>
    private static readonly string[] NetworkInputs =
        { "A0", "A1", "A2", "A3", "B0", "B1", "B2", "B3", "Cin" };

    private static readonly string[] GateNames = Enumerable.Range(0, 4).SelectMany(StageGateNames).ToArray();
    private static readonly string[] NotGateNames = GateNames.Where(n => n.Contains("CARRY") || n.Contains("ORNOT")).ToArray();

    /// <summary>The persisted signal names per gate group (issues #1025/#1034); missing = no named pins.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> ExpectedSignalNames =
        BuildExpectedSignalNames();

    /// <summary>The persisted output signal names per gate group (issue #1046); missing = no named outputs.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> ExpectedOutputSignalNames =
        Enumerable.Range(0, 4).ToDictionary(stage => $"T{stage}H2SUM", stage => $"S{stage}")
            .Append(new KeyValuePair<string, string>("T3OROUT", "Cout"))
            .ToDictionary(pair => pair.Key, pair => new Dictionary<string, string> { ["Y"] = pair.Value });

    private readonly FourBitAdderFixture _fixture;

    /// <summary>Attaches the shared 4-bit-adder fixture.</summary>
    public LogicGateFourBitAdderExampleTests(FourBitAdderFixture fixture) => _fixture = fixture;

    [Fact]
    public void Example_LoadsOnlyTopLevelGateGroups_EachWithPersistedRoles()
    {
        _fixture.Canvas.Components.ShouldAllBe(
            c => c.Component is ComponentGroup, "the 4-bit adder contains only top-level gate groups");
        _fixture.Canvas.Connections.Count.ShouldBe(339, "339 wires join the 344 gates");
        var groups = _fixture.Groups;
        groups.Count.ShouldBe(344, "four stages × (32-base + duplicated carry copies)");
        groups.Select(g => g.GroupName).ShouldBe(GateNames, ignoreOrder: true);
        foreach (var group in groups)
        {
            var roles = group.TruthTablePinAssignment.ShouldNotBeNull(
                $"group '{group.GroupName}' must ship its persisted roles");
            var isNot = NotGateNames.Contains(group.GroupName);
            roles.InputPinNames.ShouldBe(isNot ? new[] { "A" } : new[] { "A", "B" });
            roles.OutputPinNames.ShouldBe(new[] { "Y" });
            roles.BiasPinNames.ShouldBe(new[] { "BIAS" });
            roles.Threshold.ShouldBe(isNot ? NotThreshold : NandThreshold);
            roles.InputSignalNames.ShouldBe(ExpectedSignalNames.GetValueOrDefault(group.GroupName),
                $"group '{group.GroupName}' ships its network-signal identity (issues #1025/#1034)");
            roles.OutputSignalNames.ShouldBe(ExpectedOutputSignalNames.GetValueOrDefault(group.GroupName),
                $"group '{group.GroupName}' ships its output signal names (issue #1046)");
            group.Description.ShouldContain("logic layer", Case.Sensitive,
                "every gate carries the education note about logic-layer composition");
            group.Description.ShouldContain(isNot ? "0.375" : "0.125");
        }
    }

    [Fact]
    public void AssembledNetwork_ExposesNineOperandSignalsAndEveryGateOutputAsTap()
    {
        _fixture.Network.InputPinNames.ShouldBe(NetworkInputs, ignoreOrder: true,
            customMessage: "the signal names merge the 261 operand pins into exactly nine network " +
                "inputs (issues #1025/#1034) — A0–A3, B0–B3 and Cin");
        _fixture.Network.OutputPinNames.ShouldBe(
            GateNames.Select(ExpectedTapName).ToArray(), ignoreOrder: true,
            customMessage: "every gate output is a tap — the named outputs read S0–S3 and Cout (issue #1046)");
    }

    /// <summary>The network tap name of one gate's output: the output signal name where one ships (#1046).</summary>
    private static string ExpectedTapName(string gateName)
    {
        for (var stage = 0; stage < 4; stage++)
        {
            if (gateName == $"T{stage}H2SUM")
                return $"S{stage}";
        }
        return gateName == "T3OROUT" ? "Cout" : $"{gateName}.Y";
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(15, 15, true)]
    [InlineData(5, 10, false)]
    [InlineData(9, 7, false)]
    [InlineData(0, 0, true)]
    [InlineData(1, 1, true)]
    [InlineData(15, 0, true)]
    [InlineData(8, 8, false)]
    [InlineData(3, 12, true)]
    public void LogicLayer_PinnedInputCombinations_YieldTheArithmeticSum(int a, int b, bool cin)
    {
        var result = _fixture.Network.Evaluate(_fixture.InputBits(a, b, cin));
        var sum = a + b + (cin ? 1 : 0);
        var carry = cin;

        for (var stage = 0; stage < 4; stage++)
        {
            var sumBit = ((sum >> stage) & 1) == 1;
            result[$"S{stage}"].ShouldBe(sumBit,
                $"S{stage} of {a} + {b} + {(cin ? 1 : 0)} = {sum}");
            result[$"T{stage}H1SUM1.Y"].ShouldBe((((a >> stage) ^ (b >> stage)) & 1) == 1,
                $"stage {stage}'s partial sum A{stage} XOR B{stage} stays readable as a tap");
            carry = ((a >> stage) & 1) + ((b >> stage) & 1) + (carry ? 1 : 0) >= 2;
            result[stage < 3 ? $"T{stage}OROUT.Y" : "Cout"].ShouldBe(carry,
                stage < 3 ? $"the ripple carry C{stage + 1} leaves stage {stage}" : "Cout of the 5-bit sum");
            for (var k = 2; stage < 3 && k <= CarryCopies[stage]; k++)
                result[$"T{stage}OROUTC{k}.Y"].ShouldBe(carry,
                    $"every duplicated carry copy of stage {stage} reads identically");
        }
    }

    [Fact]
    public async Task SaveLoadRoundTrip_ReAssembledNetwork_YieldsTheSameFourBitAdder()
    {
        var savedPath = await _fixture.SaveToTempFile();
        try
        {
            var reloadedCanvas = await LogicGateHalfAdderExampleTests.LoadCanvas(savedPath);
            var reloadedGroups = LogicGateHalfAdderExampleTests.GroupsOf(reloadedCanvas);
            reloadedGroups.Select(g => g.GroupName).ShouldBe(GateNames, ignoreOrder: true);
            reloadedGroups.ShouldAllBe(g => g.TruthTablePinAssignment != null,
                "the persisted pin roles must survive the round trip");
            foreach (var group in reloadedGroups)
            {
                group.TruthTablePinAssignment!.InputSignalNames.ShouldBe(
                    ExpectedSignalNames.GetValueOrDefault(group.GroupName),
                    $"the signal names of '{group.GroupName}' must survive the round trip (#1025/#1034)");
                group.TruthTablePinAssignment!.OutputSignalNames.ShouldBe(
                    ExpectedOutputSignalNames.GetValueOrDefault(group.GroupName),
                    $"the output signal names of '{group.GroupName}' must survive the round trip (#1046)");
            }
            reloadedCanvas.Connections.Count.ShouldBe(339, "every gate wire must survive the round trip");

            var watch = Stopwatch.StartNew();
            var reloaded = await AssembleNetwork(reloadedCanvas);
            watch.Stop();
            _fixture.AssemblyElapsed.ShouldBeGreaterThan(TimeSpan.Zero,
                "the fixture records the assembler wall clock for the PR's scale report");
            Console.WriteLine($"[scale] 4-bit adder assembly ({GateNames.Length} gates): "
                + $"first {_fixture.AssemblyElapsed.TotalMilliseconds:F0} ms, "
                + $"after save/load round-trip {watch.Elapsed.TotalMilliseconds:F0} ms");

            reloaded.InputPinNames.ShouldBe(_fixture.Network.InputPinNames);
            reloaded.OutputPinNames.ShouldBe(_fixture.Network.OutputPinNames);
            foreach (var a in new[] { 0, 1, 7, 8, 15 })
            foreach (var b in new[] { 0, 1, 7, 8, 15 })
            foreach (var cin in new[] { false, true })
            {
                var expected = _fixture.Network.Evaluate(_fixture.InputBits(a, b, cin));
                reloaded.Evaluate(_fixture.InputBits(a, b, cin)).ShouldBe(expected,
                    $"the re-assembled network must evaluate identically for A={a}, B={b}, Cin={cin}");
            }
        }
        finally
        {
            if (File.Exists(savedPath)) File.Delete(savedPath);
        }
    }

    /// <summary>Assembles the logic network exactly as the canvas states it: the merged
    /// <see cref="LogicNetworkAssembler"/> re-extracts every gate at its persisted roles.</summary>
    internal static async Task<LogicNetworkEvaluator> AssembleNetwork(DesignCanvasViewModel canvas) =>
        await new LogicNetworkAssembler().AssembleAsync(
            canvas.Components.Select(c => c.Component).ToList(),
            canvas.Connections.Select(c => c.Connection).ToList(),
            FourBitAdderFixture.WavelengthNm);

    /// <summary>
    /// The expected persisted signal names per gate group (missing key = no named pins):
    /// every stage's operand pins carry the addend bits A{stage} and B{stage}; stage 0's
    /// second half-adder reads the adder's carry-in at its A pins (signal Cin), while
    /// stages 1–3 read the previous stage's carry-out through a wire. Fully wired pins
    /// (sum stages, carries, OR trees) ship no names — a driven pin needs no identity.
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>> BuildExpectedSignalNames()
    {
        var expected = new Dictionary<string, Dictionary<string, string>>();
        for (var stage = 0; stage < 4; stage++)
        {
            var p = $"T{stage}";
            var both = new Dictionary<string, string> { ["A"] = $"A{stage}", ["B"] = $"B{stage}" };
            expected[$"{p}H1N5"] = both;
            for (var k = 2; k <= CarryCopies[stage]; k++)
                expected[$"{p}H1N5C{k}"] = both;
            for (var j = 1; j <= 3 + CarryCopies[stage]; j++)
            {
                expected[$"{p}H1N1A{j}"] = both;
                expected[$"{p}H1N1B{j}"] = both;
                expected[$"{p}H1N2{j}"] = new() { ["A"] = $"A{stage}" };
                expected[$"{p}H1N3{j}"] = new() { ["B"] = $"B{stage}" };
            }
        }
        var cin = new Dictionary<string, string> { ["A"] = "Cin" };
        foreach (var n in new[] { "H2N1A", "H2N1B", "H2N2", "H2N5" })
            expected[$"T0{n}"] = cin;
        for (var k = 2; k <= CarryCopies[0]; k++)
            expected[$"T0H2N5C{k}"] = cin;
        return expected;
    }

    private static IEnumerable<string> StageGateNames(int stage)
    {
        var p = $"T{stage}";
        for (var j = 1; j <= 3 + CarryCopies[stage]; j++)
            foreach (var n in new[] { $"H1N1A{j}", $"H1N1B{j}", $"H1N2{j}", $"H1N3{j}", $"H1SUM{j}" })
                yield return p + n;
        foreach (var n in new[] { "H1N5", "H1CARRY", "H2N1A", "H2N1B", "H2N2", "H2N3", "H2SUM", "H2N5", "H2CARRY", "ORNOT1", "ORNOT2", "OROUT" })
            yield return p + n;
        for (var k = 2; k <= CarryCopies[stage]; k++)
            foreach (var n in new[] { $"H1N5C{k}", $"H1CARRYC{k}", $"H2N5C{k}", $"H2CARRYC{k}", $"ORNOT1C{k}", $"ORNOT2C{k}", $"OROUTC{k}" })
                yield return p + n;
    }

    /// <summary>Shared fixture: loads the shipped example once and assembles its logic network (each
    /// extraction is a real simulation run); the assembly wall clock feeds the PR's scale report.</summary>
    public class FourBitAdderFixture : IAsyncLifetime
    {
        private const string ExampleFileName = "Logic Gate 4-Bit Adder.lun";

        /// <summary>Laser wavelength the persisted roles were extracted at.</summary>
        public const int WavelengthNm = 1550;

        /// <summary>The canvas the shipped example loaded onto.</summary>
        public DesignCanvasViewModel Canvas { get; private set; } = null!;

        /// <summary>The loaded top-level gate groups, in file order.</summary>
        public List<ComponentGroup> Groups { get; private set; } = null!;

        /// <summary>The logic network assembled from the loaded design.</summary>
        public LogicNetworkEvaluator Network { get; private set; } = null!;

        /// <summary>Wall clock of the <see cref="LogicNetworkAssembler"/> on the shipped example.</summary>
        public TimeSpan AssemblyElapsed { get; private set; }

        /// <summary>Loads the shipped example and assembles its logic network, recording the wall clock.</summary>
        public async Task InitializeAsync()
        {
            var path = Path.Combine(ExampleDesignFilesTests.ExamplesDirectory(), ExampleFileName);
            Canvas = await LogicGateHalfAdderExampleTests.LoadCanvas(path);
            Groups = LogicGateHalfAdderExampleTests.GroupsOf(Canvas);
            var watch = Stopwatch.StartNew();
            Network = await AssembleNetwork(Canvas);
            watch.Stop();
            AssemblyElapsed = watch.Elapsed;
        }

        /// <summary>No shared state to release.</summary>
        public Task DisposeAsync() => Task.CompletedTask;

        /// <summary>The network input bits for one operand triple — one bit per signal (#1025/#1034).</summary>
        public Dictionary<string, bool> InputBits(int a, int b, bool cin)
        {
            var bits = new Dictionary<string, bool> { ["Cin"] = cin };
            for (var stage = 0; stage < 4; stage++)
            {
                bits[$"A{stage}"] = ((a >> stage) & 1) == 1;
                bits[$"B{stage}"] = ((b >> stage) & 1) == 1;
            }
            return bits;
        }

        /// <summary>Saves the loaded design through the real save path and returns the file path.</summary>
        public async Task<string> SaveToTempFile()
        {
            var path = Path.Combine(Path.GetTempPath(), $"four-bit-adder-{Guid.NewGuid():N}.lun");
            var saveVm = LogicGateHalfAdderExampleTests.CreateFileOperations(Canvas);
            var dialog = new Mock<IFileDialogService>();
            dialog.Setup(f => f.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(path);
            saveVm.FileDialogService = dialog.Object;
            await saveVm.SaveDesignAsCommand.ExecuteAsync(null);
            File.Exists(path).ShouldBeTrue("the real save path must write the temp .lun");
            return path;
        }
    }
}
