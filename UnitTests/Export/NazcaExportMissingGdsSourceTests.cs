using System.Collections.ObjectModel;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Export;

/// <summary>
/// The plain Nazca export (<c>FileOperationsViewModel.ExportNazcaCommand</c>): a raw-code
/// component whose geometry source (the .gds its raw code loads) is missing exports as a
/// placeholder box. The detailed description goes to the Error Console AND the aggregated
/// count must ride the final status line — previously the warning was console-only, so
/// "exported with placeholder boxes" was invisible without watching the console. The
/// gdsfactory mixed-backend side of this contract is covered by
/// <c>MixedBackendExportViewModelTests.Export_MixedBackendWithMissingGdsSource_WarnsInConsoleAndStatus</c>.
/// </summary>
public class NazcaExportMissingGdsSourceTests
{
    /// <summary>Pin the UI language so status-text assertions match the English literals.</summary>
    public NazcaExportMissingGdsSourceTests()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
    }

    private sealed class FixedPathFileDialog : IFileDialogService
    {
        private readonly string? _path;
        public FixedPathFileDialog(string? path) => _path = path;

        public Task<string?> ShowSaveFileDialogAsync(string title, string defaultExtension, string filters) =>
            Task.FromResult(_path);

        public Task<string?> ShowOpenFileDialogAsync(string title, string filters) =>
            Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task ExportNazca_DeletedGdsSource_WarnsInConsoleAndStatus()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"lunima-nazca-{Guid.NewGuid():N}.py");
        var missingGds = Path.Combine(Path.GetTempPath(), $"deleted-{Guid.NewGuid():N}.gds"); // never written
        try
        {
            var canvas = new DesignCanvasViewModel();
            canvas.Components.Add(new ComponentViewModel(RawCodeComponent()));
            var library = new ObservableCollection<ComponentTemplate> { RawCodeTemplate(missingGds) };
            var errorConsole = new ErrorConsoleService();
            var fileOps = new FileOperationsViewModel(
                canvas,
                new CommandManager(),
                new SimpleNazcaExporter(),
                new SaxExporter(),
                library,
                new GdsExportViewModel(new GdsExportService()),
                new PhotonTorchExportViewModel(new PhotonTorchExporter(), canvas),
                null!,
                errorConsole: errorConsole);
            fileOps.FileDialogService = new FixedPathFileDialog(scriptPath);
            fileOps.GdsExport.GenerateGdsEnabled = false;   // script-only, deterministic and fast
            string? lastStatus = null;
            fileOps.UpdateStatus = s => lastStatus = s;

            await fileOps.ExportNazcaCommand.ExecuteAsync(null);

            File.Exists(scriptPath).ShouldBeTrue();   // export still ran
            // Detailed per-component description → Error Console (as before)…
            errorConsole.Entries.ShouldContain(e =>
                e.Level == CAP_Contracts.Logger.LogLevel.Warn
                && e.Message.Contains("wgA")
                && e.Message.Contains(missingGds));
            // …and the aggregated count on the final status line (the new part).
            lastStatus.ShouldNotBeNull();
            lastStatus!.ShouldContain("1 component(s)");
            lastStatus.ShouldContain("placeholder box");
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    /// <summary>A placed 10×4 µm component of <see cref="RawCodeTemplate"/>.</summary>
    private static Component RawCodeComponent()
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());
        var comp = new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: "nazca_wga",
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: "wgA_1",
            rotationCounterClock: DiscreteRotation.R0)
        {
            PhysicalX = 0,
            PhysicalY = 0,
            WidthMicrometers = 10,
            HeightMicrometers = 4,
        };
        comp.PhysicalPins.Add(new PhysicalPin
        {
            Name = "in", ParentComponent = comp,
            OffsetXMicrometers = 0, OffsetYMicrometers = 2, AngleDegrees = 180,
        });
        comp.PhysicalPins.Add(new PhysicalPin
        {
            Name = "out", ParentComponent = comp,
            OffsetXMicrometers = 10, OffsetYMicrometers = 2, AngleDegrees = 0,
        });
        return comp;
    }

    /// <summary>A nazca-backend raw-code template shaped like a GDS import, loading a
    /// .gds file that does not exist (the fallback path under test).</summary>
    private static ComponentTemplate RawCodeTemplate(string missingGdsPath) => new()
    {
        Name = "wgA",
        PdkSource = "GDS Import - circuit",
        WidthMicrometers = 10,
        HeightMicrometers = 4,
        PinDefinitions = new[]
        {
            new PinDefinition("in", 0, 2, 180),
            new PinDefinition("out", 10, 2, 0),
        },
        RawCode =
            "import nazca as nd\n" +
            "\n" +
            "def component():\n" +
            "    with nd.Cell(name=\"wgA_aligned\") as cell:\n" +
            $"        _loaded = nd.load_gds(filename=\"{missingGdsPath.Replace("\\", "\\\\")}\", cellname=\"wgA\", topcellsonly=False)\n" +
            "        _bb = _loaded.bbox\n" +
            "        _loaded.put(-_bb[0], -_bb[1])\n" +
            "    return cell\n",
        RawCodeBackend = "nazca",
    };
}
