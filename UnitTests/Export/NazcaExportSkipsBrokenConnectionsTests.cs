using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Export;

/// <summary>
/// The plain Nazca export (<c>FileOperationsViewModel.ExportNazcaCommand</c>) must skip
/// blocked/invalid/routeless connections from the generated script and warn the user with
/// the count and affected pins — the gdsfactory export side of this contract is covered by
/// <c>GdsFactoryExportViewModelTests.Export_BlockedConnection_WarnsAndOmitsItFromTheGds</c>.
/// </summary>
public class NazcaExportSkipsBrokenConnectionsTests
{
    /// <summary>Pin the UI language so status-text assertions match the English literals
    /// regardless of the runner's locale (LocalizationService.Instance is process-wide).</summary>
    public NazcaExportSkipsBrokenConnectionsTests()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
    }

    private sealed class FixedPathFileDialog : IFileDialogService
    {
        private readonly string _path;
        public FixedPathFileDialog(string path) => _path = path;

        public Task<string?> ShowSaveFileDialogAsync(string title, string defaultExtension, string filters) =>
            Task.FromResult<string?>(_path);

        public Task<string?> ShowOpenFileDialogAsync(string title, string filters) =>
            Task.FromResult<string?>(null);
    }

    /// <summary>Two components joined by one blocked connection — the script generation
    /// step never touches Python, so <c>GenerateGdsEnabled = false</c> keeps this fast.</summary>
    private static DesignCanvasViewModel CanvasWithBlockedConnection()
    {
        var canvas = new DesignCanvasViewModel();
        var a = TestComponentFactory.CreateBasicComponent();
        a.Identifier = "CompA";
        a.PhysicalPins.Add(new PhysicalPin { Name = "p0", ParentComponent = a });
        var b = TestComponentFactory.CreateBasicComponent();
        b.Identifier = "CompB";
        b.PhysicalX = 200;
        b.PhysicalPins.Add(new PhysicalPin { Name = "p0", ParentComponent = b });
        canvas.AddComponent(a, "MMI A");
        canvas.AddComponent(b, "MMI B");

        var connection = new WaveguideConnection { StartPin = a.PhysicalPins[0], EndPin = b.PhysicalPins[0] };
        var path = new RoutedPath { IsBlockedFallback = true };
        path.Segments.Add(new StraightSegment(0, 0, 1, 0, 0));
        connection.RestoreCachedPath(path);
        canvas.Connections.Add(new WaveguideConnectionViewModel(connection));
        return canvas;
    }

    [Fact]
    public async Task ExportNazca_BlockedConnection_WarnsAndOmitsItFromTheScript()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"lunima-nazca-{Guid.NewGuid():N}.py");
        try
        {
            var main = MainViewModelTestHelper.CreateMainViewModel(canvas: CanvasWithBlockedConnection());
            var fileOps = main.FileOperations;
            fileOps.FileDialogService = new FixedPathFileDialog(scriptPath);
            fileOps.GdsExport.GenerateGdsEnabled = false;   // script-only, deterministic and fast
            string? lastStatus = null;
            fileOps.UpdateStatus = s => lastStatus = s;

            await fileOps.ExportNazcaCommand.ExecuteAsync(null);

            File.Exists(scriptPath).ShouldBeTrue();   // export still ran
            (await File.ReadAllTextAsync(scriptPath)).ShouldNotContain("nd.strt(");
            lastStatus.ShouldNotBeNull();
            lastStatus!.ShouldContain("1 connection(s)");
            lastStatus.ShouldContain("CompA.p0");
            lastStatus.ShouldContain("CompB.p0");
            main.ErrorConsole.Entries.ShouldContain(e =>
                e.Level == CAP_Contracts.Logger.LogLevel.Warn
                && e.Message.Contains("CompA.p0")
                && e.Message.Contains("CompB.p0"));
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task ExportNazca_AllConnectionsValid_NoSkippedConnectionsWarning()
    {
        var canvas = CanvasWithBlockedConnection();
        var connection = canvas.Connections[0].Connection;
        connection.RestoreCachedPath(new RoutedPath { Segments = { new StraightSegment(0, 0, 1, 0, 0) } });

        var scriptPath = Path.Combine(Path.GetTempPath(), $"lunima-nazca-{Guid.NewGuid():N}.py");
        try
        {
            var main = MainViewModelTestHelper.CreateMainViewModel(canvas: canvas);
            var fileOps = main.FileOperations;
            fileOps.FileDialogService = new FixedPathFileDialog(scriptPath);
            fileOps.GdsExport.GenerateGdsEnabled = false;
            string? lastStatus = null;
            fileOps.UpdateStatus = s => lastStatus = s;

            await fileOps.ExportNazcaCommand.ExecuteAsync(null);

            lastStatus.ShouldNotBeNull();
            lastStatus!.ShouldNotContain("connection(s)");
            main.ErrorConsole.Entries.ShouldBeEmpty();
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }
}
