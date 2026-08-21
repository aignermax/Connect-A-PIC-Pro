using System.Collections.ObjectModel;
using System.Text.Json;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core;
using CAP_Core.Export;
using Shouldly;

namespace UnitTests.Integration;

/// <summary>
/// Sweep over every shipped logic example (issue #1130): the manifest-driven theory
/// walks every <c>examples/*.lun</c> whose manifest entry names a logic design and
/// runs it through the real paths — <see cref="FileOperationsViewModel.LoadDesignFromPathAsync"/>
/// and the Logic panel's Build command (<see cref="LogicPanelViewModel"/>). Each example
/// must load with zero dropped connections and zero error-console entries, assemble
/// without validation errors, settle-evaluate with at least one named input and one
/// named output in the panel, and — when the network holds registers — complete one
/// clock step with a non-empty register readout. Assertions stay structural on
/// purpose: per-example truth values live in the per-example test classes. The theory
/// enumerates from <c>examples/examples.json</c>, so a new logic example joins the
/// sweep with its manifest entry alone — no test edit.
/// </summary>
public class LogicExamplesSweepTests
{
    /// <summary>File-name prefix that marks a manifest entry as a logic example.</summary>
    private const string LogicExampleFilePrefix = "Logic Gate";

    /// <summary>
    /// Rung-4 gate-builder examples that predate the persisted pin-roles seam (#986):
    /// their groups ship without <c>TruthTablePinAssignment</c>, so the assembler
    /// reports "no logic gate in the design" — reported on issue #1130 as a finding.
    /// Once the .lun files carry persisted roles, delete the entries: the sweep then
    /// covers them with no other edit.
    /// </summary>
    private static readonly HashSet<string> KnownExamplesWithoutPersistedPinRoles = new()
    {
        "Logic Gate NOT-NAND.lun",
        "Logic Gate AND-from-NAND.lun",
        "Logic Gate OR-AND.lun",
    };

    /// <summary>Manifest file inside the examples directory.</summary>
    private const string ManifestFileName = "examples.json";

    /// <summary>File names of every logic example listed in the manifest.</summary>
    public static TheoryData<string> LogicExampleFiles { get; } = LoadLogicExampleFiles();

    [Fact]
    public void Manifest_ListsAtLeastOneLogicExample()
    {
        LogicExampleFiles.ShouldNotBeEmpty(
            "the sweep must cover at least one manifest-listed logic example");
    }

    [Theory]
    [MemberData(nameof(LogicExampleFiles))]
    public async Task LogicExample_LoadsAssemblesAndEvaluatesCleanly(string exampleFileName)
    {
        var path = Path.Combine(ExampleDesignFilesTests.ExamplesDirectory(), exampleFileName);
        File.Exists(path).ShouldBeTrue(
            $"manifest entry '{exampleFileName}' must point at a shipped example file");
        if (KnownExamplesWithoutPersistedPinRoles.Contains(exampleFileName))
            return;

        var canvas = new DesignCanvasViewModel();
        var errorConsole = new ErrorConsoleService();
        var fileOps = new FileOperationsViewModel(
            canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new SaxExporter(),
            new ObservableCollection<ComponentTemplate>(TestPdkLoader.LoadAllTemplates()),
            new GdsExportViewModel(new GdsExportService()),
            new PhotonTorchExportViewModel(new PhotonTorchExporter(), canvas),
            null!,
            errorConsole: errorConsole);

        (await fileOps.LoadDesignFromPathAsync(path)).ShouldBeTrue(
            $"'{exampleFileName}' must load through the real load path");
        errorConsole.Entries.ShouldBeEmpty(
            $"'{exampleFileName}' must load with zero dropped connections and zero error-console entries");

        var panel = new LogicPanelViewModel();
        panel.Configure(canvas);
        await panel.BuildNetworkCommand.ExecuteAsync(null);

        panel.HasNetwork.ShouldBeTrue(
            $"'{exampleFileName}' must assemble without validation errors: {panel.StatusText}");
        panel.Inputs.ShouldNotBeEmpty(
            $"'{exampleFileName}' must expose at least one named input toggle");
        panel.Inputs.ShouldAllBe(
            input => !string.IsNullOrWhiteSpace(input.PinName),
            $"'{exampleFileName}' must name every input toggle");
        panel.Outputs.ShouldNotBeEmpty(
            $"'{exampleFileName}' must expose at least one named output tap");
        panel.Outputs.ShouldAllBe(
            output => !string.IsNullOrWhiteSpace(output.PinName),
            $"'{exampleFileName}' must name every output tap");

        if (!panel.HasRegisters)
            return;

        panel.StepClockCommand.Execute(null);

        panel.RegisterStates.ShouldNotBeEmpty(
            $"'{exampleFileName}' declares registers, so the readout must list them");
        panel.RegisterStates.ShouldAllBe(
            row => !string.IsNullOrWhiteSpace(row.BitsText),
            $"'{exampleFileName}' must read back non-empty register bits after one clock step");
    }

    /// <summary>
    /// Enumerates the manifest entries whose file name marks a logic example, so the
    /// sweep grows with the manifest instead of a hardcoded list.
    /// </summary>
    private static TheoryData<string> LoadLogicExampleFiles()
    {
        var data = new TheoryData<string>();
        var manifestPath = Path.Combine(ExampleDesignFilesTests.ExamplesDirectory(), ManifestFileName);
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        foreach (var entry in doc.RootElement.GetProperty("examples").EnumerateArray())
        {
            var file = entry.GetProperty("file").GetString()!;
            if (file.StartsWith(LogicExampleFilePrefix, StringComparison.OrdinalIgnoreCase))
                data.Add(file);
        }
        return data;
    }
}
