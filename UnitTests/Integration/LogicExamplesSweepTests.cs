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
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// E2E sweep over every shipped logic example (issue #1130): membership comes from
/// the <c>examples/examples.json</c> manifest — every curated entry above the
/// <c>Basics</c> rungs joins automatically, so a new example needs no test edit.
/// Each example runs the real paths end to end: it loads through
/// <see cref="FileOperationsViewModel.LoadDesignFromPathAsync"/> with zero dropped
/// connections and zero error-console entries, its logic network assembles through
/// the Logic panel's Build command without validation errors, settle evaluation runs,
/// and the panel shows at least one named network input and one named output. When
/// the network contains registers, one clock step completes and the register readout
/// is non-empty. Assertions stay structural; per-example truth values live in the
/// per-example pinned tests.
/// </summary>
public class LogicExamplesSweepTests
{
    private const string BasicsLevelName = "Basics";

    /// <summary>
    /// Manifest-derived theory rows: every curated manifest entry above the
    /// <c>Basics</c> rungs (Adders / Datapath / Sequential). The Basics rungs —
    /// the MZI and the single-group gate demos — carry no persisted gate
    /// assignment and are pinned by their own extraction-based tests.
    /// </summary>
    public static IEnumerable<object[]> LogicExamplePaths()
    {
        var examplesDir = ExampleDesignFilesTests.ExamplesDirectory();
        var manifestPath = Path.Combine(examplesDir, "examples.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        foreach (var entry in doc.RootElement.GetProperty("examples").EnumerateArray())
        {
            var name = entry.GetProperty("file").GetString();
            var level = entry.TryGetProperty("level", out var levelElement)
                ? levelElement.GetString() : null;
            if (!string.IsNullOrEmpty(name) && level != BasicsLevelName)
                yield return new object[] { Path.Combine(examplesDir, name) };
        }
    }

    [Theory]
    [MemberData(nameof(LogicExamplePaths))]
    public async Task LogicExample_LoadsAssemblesAndEvaluates_Cleanly(string examplePath) =>
        await SweepOne(examplePath);

    /// <summary>
    /// Guard against a silently vacuous register sweep: the manifest membership
    /// must keep at least one example that designates registers, so the register
    /// branch of the theory actually executes.
    /// </summary>
    [Fact]
    public async Task Sweep_ExercisesRegisterStep_OnAtLeastOneExample()
    {
        var steppedOne = false;
        foreach (var row in LogicExamplePaths())
        {
            steppedOne |= await SweepOne((string)row[0]);
        }
        steppedOne.ShouldBeTrue("the sweep must exercise the register-step path on at least one manifest example");
    }

    /// <summary>
    /// Runs the full sweep for one example and returns whether its network
    /// carried registers (so the theory's register branch executed).
    /// </summary>
    private static async Task<bool> SweepOne(string examplePath)
    {
        var errorConsole = new ErrorConsoleService();
        var canvas = new DesignCanvasViewModel();
        var fileOps = CreateFileOperations(canvas, errorConsole);

        var name = Path.GetFileName(examplePath);
        var declaredConnections = DeclaredConnections(examplePath);

        (await fileOps.LoadDesignFromPathAsync(examplePath)).ShouldBeTrue(
            $"Example '{name}' must load through the real load path");
        canvas.Connections.Count.ShouldBe(declaredConnections,
            $"Example '{name}' must keep every declared connection (none dropped)");
        errorConsole.Entries.ShouldBeEmpty(
            $"Example '{name}' must load with zero error-console entries");

        var panel = new LogicPanelViewModel();
        panel.Configure(canvas);
        await panel.BuildNetworkCommand.ExecuteAsync(null);

        panel.HasNetwork.ShouldBeTrue(panel.StatusText);
        panel.Inputs.Count.ShouldBeGreaterThan(0,
            $"Example '{name}' must expose at least one named network input");
        panel.Outputs.Count.ShouldBeGreaterThan(0,
            $"Example '{name}' must expose at least one named network output");

        if (!panel.HasRegisters)
            return false;
        panel.StepClockCommand.Execute(null);
        panel.RegisterStates.ShouldNotBeEmpty(
            $"Example '{name}' must keep a non-empty register readout across the clock step");
        return true;
    }

    /// <summary>The .lun file's own declared top-level connection count (same expectation as <see cref="ExampleDesignFilesTests"/>).</summary>
    private static int DeclaredConnections(string examplePath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(examplePath));
        return doc.RootElement.GetProperty("Connections").GetArrayLength();
    }

    /// <summary>Builds a load ViewModel over the full template library, mirroring <see cref="ExampleDesignFilesTests"/>.</summary>
    private static FileOperationsViewModel CreateFileOperations(
        DesignCanvasViewModel canvas, ErrorConsoleService errorConsole)
    {
        var library = new ObservableCollection<ComponentTemplate>(TestPdkLoader.LoadAllTemplates());
        return new FileOperationsViewModel(
            canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new SaxExporter(),
            library,
            new GdsExportViewModel(new GdsExportService()),
            new PhotonTorchExportViewModel(new PhotonTorchExporter(), canvas),
            null!,
            errorConsole);
    }
}
