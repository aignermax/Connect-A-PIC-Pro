using System.Collections.ObjectModel;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Export;
using Shouldly;

namespace UnitTests.Integration;

/// <summary>
/// Guards the shipped example designs in the repo's <c>examples/</c> directory:
/// at least one example exists, and every example loads against the full
/// template library with a meaningful circuit (components and connections).
/// Fails when an example goes stale after a template rename or format change.
/// </summary>
public class ExampleDesignFilesTests
{
    /// <summary>Minimum components a shipped example must contain to be meaningful.</summary>
    private const int MinComponentsPerExample = 4;

    /// <summary>Minimum connections a shipped example must contain to be meaningful.</summary>
    private const int MinConnectionsPerExample = 3;

    private static string ExamplesDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "examples");
            if (Directory.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException(
            "Repo examples/ directory not found walking up from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void ExamplesDirectory_ContainsAtLeastOneDesign()
    {
        Directory.GetFiles(ExamplesDirectory(), "*.lun").ShouldNotBeEmpty();
    }

    [Fact]
    public async Task EveryExample_LoadsExactlyWhatItDeclares()
    {
        var library = new ObservableCollection<ComponentTemplate>(TestPdkLoader.LoadAllTemplates());

        foreach (var examplePath in Directory.GetFiles(ExamplesDirectory(), "*.lun"))
        {
            var name = Path.GetFileName(examplePath);

            // The file's own declared contents are the expectation: a silently
            // skipped (unresolvable) component or dropped connection must fail.
            using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(examplePath));
            var declaredComponents = doc.RootElement.GetProperty("Components").GetArrayLength();
            var declaredConnections = doc.RootElement.GetProperty("Connections").GetArrayLength();
            var declaredGroups = doc.RootElement.TryGetProperty("Groups", out var groups)
                ? groups.GetArrayLength()
                : 0;

            var canvas = new DesignCanvasViewModel();
            var fileOps = new FileOperationsViewModel(
                canvas,
                new CommandManager(),
                new SimpleNazcaExporter(),
                new SaxExporter(),
                library,
                new GdsExportViewModel(new GdsExportService()),
                new PhotonTorchExportViewModel(new PhotonTorchExporter(), canvas),
                null!);

            var loaded = await fileOps.LoadDesignFromPathAsync(examplePath);

            loaded.ShouldBeTrue($"Example '{name}' must load");
            declaredComponents.ShouldBeGreaterThanOrEqualTo(
                MinComponentsPerExample, $"Example '{name}' must ship a meaningful circuit");
            declaredConnections.ShouldBeGreaterThanOrEqualTo(
                MinConnectionsPerExample, $"Example '{name}' must ship a connected circuit");
            canvas.Components.Count.ShouldBe(
                declaredComponents + declaredGroups,
                $"Example '{name}' must resolve every declared component template");
            canvas.Connections.Count.ShouldBe(
                declaredConnections,
                $"Example '{name}' must keep every declared connection");
        }
    }
}
