using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using UnitTests.Analysis.LogicAnalysis;

namespace UnitTests.Helpers;

/// <summary>
/// Shared wiring for the Logic panel's sequential tests (issues #1099, #1111): the
/// two-register ring (R1.y → R2.a, R2.y → R1.a) built from OR-reading combiner
/// groups — the physically honest stand-in for the toggle loop <c>reg = NOT(reg)</c>,
/// since the passive fixture gates cannot invert: once seeded, each register samples
/// the other's committed output, so every clock flips every register exactly once.
/// </summary>
public static class LogicRingTestFixture
{
    private const double OrThreshold = 0.25;

    /// <summary>The two-register ring on a fresh canvas: R1.y → R2.a and R2.y → R1.a.</summary>
    public static DesignCanvasViewModel RingCanvas()
    {
        var first = OrGate("R1", isRegister: true);
        var second = OrGate("R2", isRegister: true);
        var canvas = new DesignCanvasViewModel();
        canvas.Components.Add(new ComponentViewModel(first));
        canvas.Components.Add(new ComponentViewModel(second));
        canvas.Connections.Add(new WaveguideConnectionViewModel(Connect(first, "y", second, "a")));
        canvas.Connections.Add(new WaveguideConnectionViewModel(Connect(second, "y", first, "a")));
        return canvas;
    }

    /// <summary>
    /// A combiner group with the OR-reading assignment persisted, as a save → load
    /// round trip would deliver it — optionally carrying the register designation.
    /// </summary>
    public static ComponentGroup OrGate(string groupName, bool isRegister)
    {
        var group = LogicGateFixtureFactory.CreateCombinerGroup();
        group.GroupName = groupName;
        group.TruthTablePinAssignment = new TruthTablePinAssignment
        {
            InputPinNames = new List<string> { "a", "b" },
            OutputPinNames = new List<string> { "y" },
            BiasPinNames = new List<string>(),
            Threshold = OrThreshold,
            IsRegister = isRegister,
        };
        group.EnsureSMatrixComputed();
        return group;
    }

    /// <summary>A design connection between two gate groups' external pins.</summary>
    private static WaveguideConnection Connect(
        ComponentGroup from, string fromPin, ComponentGroup to, string toPin) =>
        new() { StartPin = Pin(from, fromPin), EndPin = Pin(to, toPin) };

    /// <summary>Looks up a group's connectable external pin.</summary>
    private static PhysicalPin Pin(ComponentGroup group, string name) =>
        group.PhysicalPins.Single(p => p.Name == name);
}
