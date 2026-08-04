using System.Collections.ObjectModel;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Export;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels;

/// <summary>
/// Unit tests for the .gds/.gdsii routing in <see cref="FileOperationsViewModel.LoadDesign"/>:
/// the open-design dialog offers GDS files next to .lun, and a GDS pick is handed
/// to the GDS import flow (<see cref="FileOperationsViewModel.OpenGdsImportRequested"/>)
/// instead of the .lun load path, which stays untouched for .lun files.
/// </summary>
public class FileOperationsGdsImportRoutingTests : IDisposable
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly FileOperationsViewModel _fileOps;
    private readonly Mock<IFileDialogService> _fileDialog;
    private readonly string _sourceDesignPath;

    public FileOperationsGdsImportRoutingTests()
    {
        _canvas = new DesignCanvasViewModel();
        _sourceDesignPath = Path.Combine(Path.GetTempPath(), $"test-gdsrouting-source-{Guid.NewGuid():N}.lun");

        _fileOps = new FileOperationsViewModel(
            _canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new SaxExporter(),
            new ObservableCollection<ComponentTemplate>(),
            new GdsExportViewModel(new GdsExportService()),
            new PhotonTorchExportViewModel(new PhotonTorchExporter(), _canvas),
            null!);

        _fileDialog = new Mock<IFileDialogService>();
        _fileOps.FileDialogService = _fileDialog.Object;
    }

    public void Dispose()
    {
        if (File.Exists(_sourceDesignPath))
        {
            File.Delete(_sourceDesignPath);
        }
    }

    private void PickFile(string? path) => _fileDialog
        .Setup(d => d.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
        .ReturnsAsync(path);

    [Theory]
    [InlineData("circuit.gds")]
    [InlineData("circuit.gdsii")]
    [InlineData("CIRCUIT.GDS")]
    public async Task LoadDesign_GdsFilePicked_RoutesToGdsImportFlow(string fileName)
    {
        var pickedPath = Path.Combine(Path.GetTempPath(), fileName);
        PickFile(pickedPath);
        string? routed = null;
        _fileOps.OpenGdsImportRequested = path =>
        {
            routed = path;
            return Task.CompletedTask;
        };
        var projectOpened = false;
        _fileOps.ProjectOpened = () => projectOpened = true;

        await _fileOps.LoadDesignCommand.ExecuteAsync(null);

        routed.ShouldBe(pickedPath, "a GDS pick goes to the import flow, not the .lun load path");
        projectOpened.ShouldBeTrue("the Home screen must let go once the import flow takes over");
        _fileOps.CurrentFilePath.ShouldBeNull("the import does not turn into a .lun project file");
        _canvas.Components.ShouldBeEmpty("nothing is loaded onto the canvas before the import runs");
    }

    [Fact]
    public async Task LoadDesign_GdsPickWithoutImportWiring_ReportsUnavailable()
    {
        PickFile(Path.Combine(Path.GetTempPath(), "circuit.gds"));
        string? status = null;
        _fileOps.UpdateStatus = s => status = s;

        await _fileOps.LoadDesignCommand.ExecuteAsync(null);

        status.ShouldBe(LocalizationService.Instance.Translate("GdsImport.StatusUnavailable"));
    }

    [Fact]
    public async Task LoadDesign_LunFilePicked_LoadsAsDesignAndSkipsGdsImport()
    {
        // A real .lun on disk: save the empty canvas, then open it via the picker.
        _fileDialog
            .Setup(d => d.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_sourceDesignPath);
        await _fileOps.SaveDesignCommand.ExecuteAsync(null);
        PickFile(_sourceDesignPath);
        var gdsImportCalled = false;
        _fileOps.OpenGdsImportRequested = _ =>
        {
            gdsImportCalled = true;
            return Task.CompletedTask;
        };
        string? status = null;
        _fileOps.UpdateStatus = s => status = s;

        await _fileOps.LoadDesignCommand.ExecuteAsync(null);

        gdsImportCalled.ShouldBeFalse("a .lun pick must stay on the .lun load path");
        status.ShouldStartWith("Loaded ");
        _fileOps.CurrentFilePath.ShouldBe(_sourceDesignPath);
    }

    [Fact]
    public async Task LoadDesign_OpenDialogFilter_OffersGdsAlongsideLun()
    {
        string? filterUsed = null;
        _fileDialog
            .Setup(d => d.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, filter) => filterUsed = filter)
            .ReturnsAsync((string?)null);

        await _fileOps.LoadDesignCommand.ExecuteAsync(null);

        filterUsed.ShouldNotBeNull("the open-design dialog ran");
        filterUsed.ShouldContain("*.lun");
        filterUsed.ShouldContain("*.gds;*.gdsii");
    }

    [Theory]
    [InlineData("design.lun", false)]
    [InlineData("layout.gds", true)]
    [InlineData("layout.gdsii", true)]
    [InlineData("LAYOUT.GDSII", true)]
    [InlineData("notes.gds.bak", false)]
    public void IsGdsFile_ClassifiesExtensions(string fileName, bool expected) =>
        FileOperationsViewModel.IsGdsFile(fileName).ShouldBe(expected);
}
