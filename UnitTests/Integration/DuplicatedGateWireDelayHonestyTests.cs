using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Honesty test for building circuits by duplicating gates (issue #1049, rung 3/4,
/// feature #1020/#1027): a gate group with persisted pin roles is duplicated via
/// <see cref="ComponentGroup.DeepCopy"/> — the way copy/paste and library instantiation
/// build adders — the copy keeps its persisted roles (a copy of a gate is a gate), moves
/// 3 mm away through the real canvas move API, and a wire from the driver's output pin to
/// the copy's input pin is routed through the real router, bound to the groups' synced
/// external pins exactly as the interactive wiring flow binds them. The assembled network
/// must report the non-zero wire delay of that wire (L·n_g/c, recomputed independently
/// from the connection), and the routed path — and with it the delay — must survive the
/// real save → load path unchanged.
/// </summary>
public class DuplicatedGateWireDelayHonestyTests : IDisposable
{
    private const double DelayTolerancePicoseconds = 1e-9;
    private const double MinRoutedLengthMicrometers = 1000;
    private const double MoveDeltaMicrometers = 3000;
    private const int WavelengthNm = 1550;
    private const string NotNandFileName = "Logic Gate NOT-NAND.lun";

    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var path in _tempFiles.Where(File.Exists))
            File.Delete(path);
    }

    [Fact]
    public async Task DuplicatedGate_SaveLoadReload_KeepsTheNonZeroWireDelay()
    {
        var driver = await LoadGateWithPersistedRoles("G1");
        var load = driver.DeepCopy();
        load.GroupName = "G2";
        load.TruthTablePinAssignment.ShouldNotBeNull(
            "a duplicated gate must keep its pin roles — otherwise it silently drops " +
            "out of the logic network");
        load.EnsureSMatrixComputed();

        var canvas = new DesignCanvasViewModel();
        canvas.AddComponent(driver);
        var loadVm = canvas.AddComponent(load);
        var oldY = load.PhysicalY;
        canvas.MoveComponent(loadVm, 0, MoveDeltaMicrometers);
        load.PhysicalY.ShouldBe(oldY + MoveDeltaMicrometers,
            "the real move API must accept the distant placement");

        var connection = (await canvas.ConnectPinsAsync(
            driver.PhysicalPins.Single(p => p.Name == "Y"),
            load.PhysicalPins.Single(p => p.Name == "A")))!.Connection;
        connection.PathLengthMicrometers.ShouldBeGreaterThan(MinRoutedLengthMicrometers,
            "the real router must route the distant pair with a substantial path");

        var before = await new LogicNetworkAssembler().AssembleAsync(
            new Component[] { driver, load }, new[] { connection }, WavelengthNm);
        var edge = before.WireDelaysPicoseconds.Keys.Single();
        before.WireDelaysPicoseconds[edge].ShouldBeGreaterThan(0);
        before.WireDelaysPicoseconds[edge].ShouldBe(
            ExpectedDelayPicoseconds(connection), DelayTolerancePicoseconds);

        var reloadedCanvas = await LogicGateHalfAdderExampleTests.LoadCanvas(await Save(canvas));
        var reloadedConnection = reloadedCanvas.Connections.Select(c => c.Connection).Single();
        reloadedConnection.PathLengthMicrometers.ShouldBeGreaterThan(MinRoutedLengthMicrometers,
            "the routed path must survive the save → reload round-trip");

        var reloaded = await LogicGateFullAdderExampleTests.AssembleNetwork(reloadedCanvas);
        var reloadedDelay = reloaded.WireDelaysPicoseconds.Values.Single();
        reloadedDelay.ShouldBeGreaterThan(0,
            "the reloaded network must keep its non-zero wire delay");
        reloadedDelay.ShouldBe(before.WireDelaysPicoseconds[edge], DelayTolerancePicoseconds,
            "the wire delay is identical after load → save → reload");
        reloadedDelay.ShouldBe(ExpectedDelayPicoseconds(reloadedConnection), DelayTolerancePicoseconds,
            "the reloaded delay still matches the reloaded connection's geometry");
    }

    /// <summary>delay = routed path length × n_g / c, recomputed from the connection itself.</summary>
    private static double ExpectedDelayPicoseconds(WaveguideConnection connection) =>
        connection.PathLengthMicrometers
        * (connection.DispersionModel?.GroupIndexAt(WavelengthNm) ?? GateDelayCalculator.DefaultGroupIndex)
        / GateDelayCalculator.SpeedOfLightMicrometersPerPicosecond;

    /// <summary>
    /// Delivers one gate group the way the interactive flow produces it: the shipped
    /// NOT/NAND example is loaded, its truth table extracted through the real panel
    /// flow (which seeds the persisted assignment), saved, and reloaded.
    /// </summary>
    private async Task<ComponentGroup> LoadGateWithPersistedRoles(string gateId)
    {
        var path = Path.Combine(ExampleDesignFilesTests.ExamplesDirectory(), NotNandFileName);
        var canvas = await LogicGateHalfAdderExampleTests.LoadCanvas(path);
        var groupVm = canvas.Components.Single(c => c.Component is ComponentGroup);
        canvas.Selection.SelectSingle(groupVm);
        var vm = new TruthTableViewModel();
        vm.ConfigureForSelection(groupVm, canvas);
        vm.IsGroupSelected.ShouldBeTrue("the loaded gate group must activate the panel");
        foreach (var name in new[] { "A", "B" })
            vm.InputPins.Single(p => p.PinName == name).IsChecked = true;
        vm.OutputPins.Single(p => p.PinName == "Y").IsChecked = true;
        vm.BiasPins.Single(p => p.PinName == "BIAS").IsChecked = true;
        vm.Threshold = 0.125;
        await vm.ExtractCommand.ExecuteAsync(null);
        vm.HasResult.ShouldBeTrue("the extraction that seeds persistence must succeed");

        ((ComponentGroup)groupVm.Component).GroupName = gateId;
        var group = LogicGateHalfAdderExampleTests.GroupsOf(
            await LogicGateHalfAdderExampleTests.LoadCanvas(await Save(canvas))).Single();
        group.TruthTablePinAssignment.ShouldNotBeNull("the reloaded group must carry the persisted roles");
        group.EnsureSMatrixComputed();
        return group;
    }

    /// <summary>Saves the canvas's design through the real save path to a temp file.</summary>
    private async Task<string> Save(DesignCanvasViewModel canvas)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wire-delay-honesty-{Guid.NewGuid():N}.lun");
        _tempFiles.Add(path);
        var saveVm = LogicGateHalfAdderExampleTests.CreateFileOperations(canvas);
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
