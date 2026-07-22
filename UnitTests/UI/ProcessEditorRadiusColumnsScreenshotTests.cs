using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.Views;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Visual check for the fabrication-process editor: the cross-section table must carry
/// clearly readable, localized "Min. Radius (µm)" / "Rec. Radius (µm)" column headers so
/// users can find where a process' minimum bend radius (e.g. Cornerstone SiN, 30 µm)
/// lives. Writes a PNG to <c>artifacts/ui-screenshots/process-editor/</c> when
/// <c>UI_SHOT_DIR</c> is set.
/// </summary>
[Trait("Category", "UiScreenshots")]
public class ProcessEditorRadiusColumnsScreenshotTests
{
    [AvaloniaFact]
    public void CaptureProcessEditorWithRadiusColumns()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UI_SHOT_DIR")))
            return; // opt-in: heavy headless render, only on explicit request (see UiScreenshotTests)
        var outputDir = ResolveOutputDirectory();
        Directory.CreateDirectory(outputDir);

        var vm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>());
        vm.Load(CreateCornerstoneLikeProcess());

        var window = new ProcessManagementWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var bitmap = window.CaptureRenderedFrame();
        window.Close();
        Dispatcher.UIThread.RunJobs();

        bitmap.ShouldNotBeNull("render miss for process editor window");
        byte[] bytes;
        using (bitmap)
            bytes = ScreenshotArtifacts.SavePng(bitmap!, Path.Combine(outputDir, "01-process-editor-radius-columns.png"));
        bytes.Length.ShouldBeGreaterThan(0);
    }

    /// <summary>A process shaped like the Cornerstone SiN PDK: optical cross-sections with
    /// a 30 µm minimum bend radius plus a metal line, so all table columns are exercised.</summary>
    private static ProcessDefinition CreateCornerstoneLikeProcess()
    {
        return new ProcessDefinition
        {
            Name = "Cornerstone SiN 300nm",
            Layers = new List<ProcessLayer>
            {
                new() { Name = "SiN_Core", Layer = 203, Datatype = 0, Description = "Silicon nitride waveguide core" },
                new() { Name = "Metal", Layer = 39, Datatype = 0, Description = "Electrical routing metal" },
            },
            Xsections = new List<ProcessXsection>
            {
                new()
                {
                    Name = "strip_sin", Kind = XsectionKind.Optical, WidthUm = 1.2,
                    MinRadiusUm = 30.0, RecommendedRadiusUm = 50.0,
                    Description = "SiN strip waveguide",
                },
                new()
                {
                    Name = "metal_dc", Kind = XsectionKind.Metal, WidthUm = 10.0,
                    Description = "DC metal line",
                },
            },
            Materials = new List<ProcessMaterial>
            {
                new() { Name = "SiN", Role = "core" },
                new() { Name = "SiO2", Role = "cladding" },
            },
        };
    }

    private static string ResolveOutputDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("UI_SHOT_DIR");
        if (!string.IsNullOrEmpty(envDir))
            return Path.Combine(envDir, "process-editor");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "artifacts", "ui-screenshots", "process-editor");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-screenshots", "process-editor");
    }
}
