using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Load honesty (issue #1109): <c>DesignCanvasViewModel.ConnectPins*</c> replaces any
/// existing wire on both endpoint pins — intended UX interactively, but on file load a
/// .lun whose netlist wires one pin more than once loads only the last wire, simulating
/// a different circuit than the file describes. The load must keep that behavior AND
/// report every displaced connection (status-bar count, error-console pin pairs) instead
/// of shrinking the netlist silently. Clean files report nothing.
/// </summary>
public class DuplicatePinConnectionLoadTests
{
    private readonly ObservableCollection<ComponentTemplate> _library =
        new(TestPdkLoader.LoadAllTemplates());

    [Fact]
    public async Task Load_OneOutputWiredToTwoInputs_KeepsLastWireAndReportsBothDrops()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"duplicate-pin-{Guid.NewGuid():N}.lun");
        try
        {
            // A real design: source out1 → mmiA.in and source out2 → mmiB.in.
            var (saveVm, saveCanvas, _) = CreateSetup();
            var (source, mmiA, mmiB) = PlaceThreeMmis(saveCanvas);
            var sourceOut1 = source.PhysicalPins.First(p => p.Name == "out1");
            var sourceOut2 = source.PhysicalPins.First(p => p.Name == "out2");
            var inA = mmiA.PhysicalPins.First(p => p.Name == "in");
            var inB = mmiB.PhysicalPins.First(p => p.Name == "in");
            saveCanvas.ConnectPins(sourceOut1, inA);
            saveCanvas.ConnectPins(sourceOut2, inB);
            await SaveToFile(saveVm, tempFile);

            // Simulate a file produced elsewhere: the source's out1 drives BOTH inputs.
            // Save wrote the surviving wire first, so duplicating its entry with the
            // input switched to mmiB makes the out1→mmiB.in wire load last and win.
            var root = JsonNode.Parse(await File.ReadAllTextAsync(tempFile))!;
            var connections = root["Connections"]!.AsArray();
            var duplicated = connections[0]!.DeepClone();
            duplicated["EndComponentId"] = mmiB.Identifier;
            duplicated["EndPinName"] = "in";
            connections.Add(duplicated);
            await File.WriteAllTextAsync(tempFile, root.ToJsonString());

            var (loadVm, loadCanvas, errorConsole) = CreateSetup();
            string? status = null;
            loadVm.UpdateStatus = s => status = s;
            await LoadFromFile(loadVm, tempFile);

            var survivor = loadCanvas.Connections.ShouldHaveSingleItem(
                "replacement-on-connect still applies during load — only the last wire survives");
            survivor.Connection.StartPin.Name.ShouldBe("out1",
                "the surviving wire must start on the doubly-wired output");
            survivor.Connection.EndPin.ParentComponent!.Identifier.ShouldBe(mmiB.Identifier,
                "the last connection in the file wins the pin");

            // ConnectPins replaces the wire on BOTH endpoints, so the last wire
            // displaces out1→mmiA.in (occupied start pin) AND out2→mmiB.in
            // (occupied end pin) — 2 of the file's 3 wires are lost and the
            // report must count both.
            status.ShouldNotBeNull("the load must surface the drop instead of staying silent");
            status.ShouldContain("2");
            var warning = errorConsole.Entries.ShouldHaveSingleItem(
                "the error console lists the dropped connections once").Message;
            warning.ShouldContain($"{source.Identifier}.out1");
            warning.ShouldContain($"{mmiA.Identifier}.in");
            warning.ShouldContain($"{source.Identifier}.out2");
            warning.ShouldContain($"{mmiB.Identifier}.in");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Load_OneOutputWiredToTwoInputsOnly_ReportsExactlyOneDrop()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"duplicate-pin-only-{Guid.NewGuid():N}.lun");
        try
        {
            var (saveVm, saveCanvas, _) = CreateSetup();
            var (source, mmiA, mmiB) = PlaceThreeMmis(saveCanvas);
            saveCanvas.ConnectPins(
                source.PhysicalPins.First(p => p.Name == "out1"),
                mmiA.PhysicalPins.First(p => p.Name == "in"));
            saveCanvas.ConnectPins(
                source.PhysicalPins.First(p => p.Name == "out2"),
                mmiB.PhysicalPins.First(p => p.Name == "in"));
            await SaveToFile(saveVm, tempFile);

            // A netlist where the source's out1 drives both inputs — and nothing else:
            // drop the second wire entirely and re-point the duplicated first at mmiB.
            var root = JsonNode.Parse(await File.ReadAllTextAsync(tempFile))!;
            var connections = root["Connections"]!.AsArray();
            var duplicated = connections[0]!.DeepClone();
            duplicated["EndComponentId"] = mmiB.Identifier;
            duplicated["EndPinName"] = "in";
            connections.RemoveAt(1);
            connections.Add(duplicated);
            await File.WriteAllTextAsync(tempFile, root.ToJsonString());

            var (loadVm, loadCanvas, errorConsole) = CreateSetup();
            string? status = null;
            loadVm.UpdateStatus = s => status = s;
            await LoadFromFile(loadVm, tempFile);

            loadCanvas.Connections.ShouldHaveSingleItem(
                "exactly one wire survives when one output drives two inputs");
            status.ShouldNotBeNull();
            status.ShouldContain("1");
            status.ShouldNotContain("2");
            var warning = errorConsole.Entries.ShouldHaveSingleItem().Message;
            warning.ShouldContain($"{source.Identifier}.out1");
            warning.ShouldContain($"{mmiA.Identifier}.in");
            warning.ShouldNotContain(mmiB.Identifier);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Load_CleanRoundTrippedDesign_ReportsNoDrops()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"clean-load-{Guid.NewGuid():N}.lun");
        try
        {
            var (saveVm, saveCanvas, _) = CreateSetup();
            var (source, mmiA, mmiB) = PlaceThreeMmis(saveCanvas);
            saveCanvas.ConnectPins(
                source.PhysicalPins.First(p => p.Name == "out1"),
                mmiA.PhysicalPins.First(p => p.Name == "in"));
            saveCanvas.ConnectPins(
                source.PhysicalPins.First(p => p.Name == "out2"),
                mmiB.PhysicalPins.First(p => p.Name == "in"));
            await SaveToFile(saveVm, tempFile);

            var (loadVm, loadCanvas, errorConsole) = CreateSetup();
            string? status = null;
            loadVm.UpdateStatus = s => status = s;
            await LoadFromFile(loadVm, tempFile);

            loadCanvas.Connections.Count.ShouldBe(2);
            errorConsole.Entries.ShouldBeEmpty("a clean file must not report any dropped connection");
            status.ShouldNotBeNull();
            status.ShouldNotContain("dropped");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Load_EveryShippedExample_ReportsNoDrops()
    {
        foreach (var examplePath in Directory.GetFiles(ExampleDesignFilesTests.ExamplesDirectory(), "*.lun"))
        {
            var (loadVm, _, errorConsole) = CreateSetup();
            (await loadVm.LoadDesignFromPathAsync(examplePath)).ShouldBeTrue(
                $"'{Path.GetFileName(examplePath)}' must load through the real load path");

            errorConsole.Entries.ShouldBeEmpty(
                $"shipped example '{Path.GetFileName(examplePath)}' must load without dropped connections");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private (Component Source, Component MmiA, Component MmiB) PlaceThreeMmis(
        DesignCanvasViewModel canvas)
    {
        var mmiTemplate = _library.First(t => t.Name == "1x2 MMI Splitter");
        var source = Place(mmiTemplate, canvas, "dup_source", 0, 0);
        var mmiA = Place(mmiTemplate, canvas, "dup_target_a", 400, 0);
        var mmiB = Place(mmiTemplate, canvas, "dup_target_b", 400, 300);
        return (source, mmiA, mmiB);
    }

    private static Component Place(
        ComponentTemplate template, DesignCanvasViewModel canvas, string identifier, double x, double y)
    {
        var component = ComponentTemplates.CreateFromTemplate(template, x, y);
        component.Identifier = identifier;
        canvas.AddComponent(component, template.Name);
        return component;
    }

    private (FileOperationsViewModel Vm, DesignCanvasViewModel Canvas, ErrorConsoleService ErrorConsole)
        CreateSetup()
    {
        var canvas = new DesignCanvasViewModel();
        var errorConsole = new ErrorConsoleService();
        var vm = new FileOperationsViewModel(
            canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new SaxExporter(),
            _library,
            new GdsExportViewModel(new GdsExportService()),
            new PhotonTorchExportViewModel(new PhotonTorchExporter(), canvas),
            null!,
            errorConsole: errorConsole);
        return (vm, canvas, errorConsole);
    }

    private static async Task SaveToFile(FileOperationsViewModel vm, string filePath)
    {
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(filePath);
        vm.FileDialogService = dialog.Object;
        await vm.SaveDesignAsCommand.ExecuteAsync(null);
        File.Exists(filePath).ShouldBeTrue("the real save path must write the temp .lun");
    }

    private static async Task LoadFromFile(FileOperationsViewModel vm, string filePath)
    {
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(filePath);
        vm.FileDialogService = dialog.Object;
        await vm.LoadDesignCommand.ExecuteAsync(null);
    }
}
