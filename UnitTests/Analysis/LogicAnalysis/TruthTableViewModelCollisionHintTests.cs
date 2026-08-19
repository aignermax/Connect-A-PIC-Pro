using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// Live signal-name collision hints in the Truth Table panel (issue #1071): while a
/// name is typed into an input or output signal field, the row warns the moment it
/// collides across the canvas's gate groups — two outputs sharing one name (output
/// names never merge) or a name spanning both an input and an output (cross-role).
/// Same-named inputs merge by design and stay silent. The hint mirrors what
/// <see cref="LogicNetworkBuilder"/> rejects at build time, and the pairing theory
/// below asserts exactly that: every warned constellation throws on build, every
/// silent one builds.
/// </summary>
public class TruthTableViewModelCollisionHintTests
{
    private readonly Dictionary<string, ComponentGroup> _groups = new();
    private DesignCanvasViewModel _canvas = null!;

    [Fact]
    public void InputName_EqualToOtherGateOutputSignal_ShowsCrossRoleHint()
    {
        TwoNotGatesOnCanvas("SUM", "INV");
        NamePin("SUM", "S", input: false);
        var vm = Configure("INV");

        vm.InputPins.Single(p => p.PinName == "A").SignalName = "S";

        var warning = vm.InputPins.Single(p => p.PinName == "A").SignalWarning;
        warning.ShouldNotBeNullOrEmpty("the name spans an input and an output — the build would reject it");
        warning.ShouldContain("'S'");
    }

    [Fact]
    public void InputName_EqualToRawOutputTap_ShowsCrossRoleHint()
    {
        TwoNotGatesOnCanvas("SUM", "INV");
        var vm = Configure("INV");

        vm.InputPins.Single(p => p.PinName == "A").SignalName = "SUM.Y";

        vm.InputPins.Single(p => p.PinName == "A").SignalWarning.ShouldNotBeNullOrEmpty(
            "an input named like the raw <gate>.<pin> tap is the same cross-role collision the builder rejects");
    }

    [Fact]
    public void OutputName_EqualToOtherGateOutputSignal_ShowsDuplicateOutputHint()
    {
        TwoNotGatesOnCanvas("SUM", "CARRY");
        NamePin("SUM", "S", input: false);
        var vm = Configure("CARRY");

        vm.OutputPins.Single(p => p.PinName == "Y").SignalName = "S";

        var warning = vm.OutputPins.Single(p => p.PinName == "Y").SignalWarning;
        warning.ShouldNotBeNullOrEmpty("two outputs sharing one tap name never merge");
        warning.ShouldContain("'S'");
    }

    [Fact]
    public void OutputName_EqualToOtherGateInputSignal_ShowsCrossRoleHint()
    {
        TwoNotGatesOnCanvas("SUM", "INV");
        NamePin("SUM", "A", input: true);
        var vm = Configure("INV");

        vm.OutputPins.Single(p => p.PinName == "Y").SignalName = "A";

        vm.OutputPins.Single(p => p.PinName == "Y").SignalWarning.ShouldNotBeNullOrEmpty(
            "the output side of a name spanning both roles warns just the same");
    }

    [Fact]
    public void TwoInputs_SameSignalName_StaySilent()
    {
        TwoNotGatesOnCanvas("INV1", "INV2");
        NamePin("INV1", "S", input: true);
        var vm = Configure("INV2");

        vm.InputPins.Single(p => p.PinName == "A").SignalName = "S";

        vm.InputPins.Single(p => p.PinName == "A").SignalWarning.ShouldBeEmpty(
            "same-named inputs are the intentional merging — no warning");
    }

    [Fact]
    public void SameGroup_TwoOutputsSharingName_ShowsDuplicateOutputHint()
    {
        TwoNotGatesOnCanvas("ADDER", "OTHER");
        var y2 = new PhysicalPin { Name = "Y2", ParentComponent = Group("ADDER") };
        Group("ADDER").PhysicalPins.Add(y2);
        Group("ADDER").AddExternalPin(new GroupPin { Name = "Y2", InternalPin = y2 });
        Group("ADDER").TruthTablePinAssignment!.OutputPinNames.Add("Y2");
        var vm = Configure("ADDER");
        vm.OutputPins.Single(p => p.PinName == "Y").SignalName = "S";

        vm.OutputPins.Single(p => p.PinName == "Y2").SignalName = "S";

        vm.OutputPins.Single(p => p.PinName == "Y2").SignalWarning.ShouldNotBeNullOrEmpty(
            "two outputs of the same gate sharing one name collide too");
        vm.OutputPins.Single(p => p.PinName == "Y").SignalWarning.ShouldNotBeNullOrEmpty(
            "the first output's row reflects the collision as well");
    }

    [Fact]
    public void ClearingTheName_ClearsTheHint()
    {
        TwoNotGatesOnCanvas("SUM", "INV");
        NamePin("SUM", "S", input: false);
        var vm = Configure("INV");
        var pin = vm.InputPins.Single(p => p.PinName == "A");
        pin.SignalName = "S";
        pin.SignalWarning.ShouldNotBeNullOrEmpty();

        pin.SignalName = "";

        pin.SignalWarning.ShouldBeEmpty("no name — nothing to collide");
    }

    [Fact]
    public void RenamingAwayFromCollision_ClearsTheHint()
    {
        TwoNotGatesOnCanvas("SUM", "INV");
        NamePin("SUM", "S", input: false);
        var vm = Configure("INV");
        var pin = vm.InputPins.Single(p => p.PinName == "A");
        pin.SignalName = "S";
        pin.SignalWarning.ShouldNotBeNullOrEmpty();

        pin.SignalName = "Ain";

        pin.SignalWarning.ShouldBeEmpty();
    }

    [Fact]
    public void Warning_DoesNotBlockTheWrite_SavingStaysAllowed()
    {
        TwoNotGatesOnCanvas("SUM", "INV");
        NamePin("SUM", "S", input: false);
        var vm = Configure("INV");

        vm.InputPins.Single(p => p.PinName == "A").SignalName = "S";

        Group("INV").TruthTablePinAssignment!.InputSignalNames.ShouldBe(
            new Dictionary<string, string> { ["A"] = "S" },
            "the warning is advisory — the name still writes through to the persisted assignment");
    }

    /// <summary>
    /// Panel hint vs. builder verdict, paired per constellation: every warned state
    /// throws on build, every silent state builds (acceptance 2).
    /// </summary>
    [Theory]
    [InlineData("DuplicateOutputs", true, true)]
    [InlineData("CrossRole", true, true)]
    [InlineData("SameNameInputs", false, false)]
    [InlineData("UniqueNames", false, false)]
    public void PanelHint_MirrorsBuilderVerdict(string constellation, bool expectWarning, bool expectBuildThrow)
    {
        TwoNotGatesOnCanvas("INV1", "INV2");
        var vm = Configure("INV2");
        PinSelectionViewModel edited;
        switch (constellation)
        {
            case "DuplicateOutputs":
                NamePin("INV1", "S", input: false);
                edited = vm.OutputPins.Single(p => p.PinName == "Y");
                edited.SignalName = "S";
                break;
            case "CrossRole":
                NamePin("INV1", "S", input: false);
                edited = vm.InputPins.Single(p => p.PinName == "A");
                edited.SignalName = "S";
                break;
            case "SameNameInputs":
                NamePin("INV1", "S", input: true);
                edited = vm.InputPins.Single(p => p.PinName == "A");
                edited.SignalName = "S";
                break;
            default:
                NamePin("INV1", "Sum", input: false);
                edited = vm.InputPins.Single(p => p.PinName == "A");
                edited.SignalName = "CarryIn";
                break;
        }

        (edited.SignalWarning.Length > 0).ShouldBe(expectWarning,
            $"constellation {constellation}: the panel hint state");
        void Build() => new LogicNetworkBuilder().Build(
            new[] { GateInstance("INV1"), GateInstance("INV2") },
            Array.Empty<WaveguideConnection>());
        if (expectBuildThrow)
            Should.Throw<ArgumentException>(Build);
        else
            Should.NotThrow(Build);
    }

    private ComponentGroup Group(string name) => _groups[name];

    /// <summary>Two persisted NOT-shaped gates placed on one shared canvas.</summary>
    private void TwoNotGatesOnCanvas(string firstName, string secondName)
    {
        _canvas = new DesignCanvasViewModel();
        foreach (var name in new[] { firstName, secondName })
        {
            var group = CreateNotGroup(name);
            _groups[name] = group;
            _canvas.AddComponent(group);
        }
    }

    /// <summary>Names one gate's A input or Y output through the panel, the way the user would.</summary>
    private void NamePin(string gateName, string signal, bool input)
    {
        var canvas = new DesignCanvasViewModel();
        var groupVm = canvas.AddComponent(Group(gateName));
        canvas.Selection.SelectSingle(groupVm);
        var vm = new TruthTableViewModel();
        vm.ConfigureForSelection(groupVm, canvas);
        (input ? vm.InputPins : vm.OutputPins).Single(p => p.PinName == (input ? "A" : "Y")).SignalName = signal;
        Group(gateName).TruthTablePinAssignment.ShouldNotBeNull(
            "the naming went through — the group carries a persisted assignment");
    }

    /// <summary>Configures the Truth Table panel for the named gate on the shared canvas.</summary>
    private TruthTableViewModel Configure(string gateName)
    {
        var groupVm = _canvas.Components.Single(c => ReferenceEquals(c.Component, Group(gateName)));
        _canvas.Selection.SelectSingle(groupVm);
        var vm = new TruthTableViewModel();
        vm.ConfigureForSelection(groupVm, canvas: _canvas);
        return vm;
    }

    /// <summary>The gate instance the assembler would build from the group's persisted roles.</summary>
    private LogicGateInstance GateInstance(string gateName)
    {
        var roles = Group(gateName).TruthTablePinAssignment!;
        return new LogicGateInstance(
            Group(gateName),
            PinnedGateTables.NotGate(),
            new GateRoleAssignment(
                roles.InputPinNames, roles.OutputPinNames, roles.BiasPinNames, roles.Threshold,
                roles.InputSignalNames, roles.OutputSignalNames));
    }

    /// <summary>A bare NOT-shaped group with persisted roles but no signal names yet.</summary>
    private static ComponentGroup CreateNotGroup(string groupName)
    {
        var group = new ComponentGroup(groupName);
        foreach (var pinName in new[] { "A", "BIAS", "Y" })
        {
            var physicalPin = new PhysicalPin { Name = pinName, ParentComponent = group };
            group.PhysicalPins.Add(physicalPin);
            group.AddExternalPin(new GroupPin { Name = pinName, InternalPin = physicalPin });
        }
        group.TruthTablePinAssignment = new TruthTablePinAssignment
        {
            InputPinNames = new List<string> { "A" },
            OutputPinNames = new List<string> { "Y" },
            BiasPinNames = new List<string> { "BIAS" },
            Threshold = PinnedGateTables.NotThreshold,
        };
        return group;
    }
}
