using CAP_Core;
using CAP_Core.Export;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Export;

/// <summary>
/// ViewModel for PhotonTorch export configuration.
/// Allows users to export photonic designs to PhotonTorch Python scripts
/// for time-domain and steady-state GPU-accelerated simulation.
/// </summary>
public partial class PhotonTorchExportViewModel : ObservableObject
{
    private readonly PhotonTorchExporter _exporter;
    private readonly DesignCanvasViewModel _canvas;
    private readonly ErrorConsoleService? _errorConsole;
    private readonly IUrlLauncher _urlLauncher;

    /// <summary>Wavelength in nanometers used for simulation.</summary>
    [ObservableProperty]
    private double _wavelengthNm = 1550.0;

    /// <summary>True for time-domain simulation; false for steady-state.</summary>
    [ObservableProperty]
    private bool _useTimeDomain;

    /// <summary>Bit rate in Gbit/s for time-domain simulation.</summary>
    [ObservableProperty]
    private double _bitRateGbps = 1.0;

    /// <summary>Number of time steps for time-domain simulation.</summary>
    [ObservableProperty]
    private int _timeDomainSteps = 1000;

    /// <summary>Status message from the last export operation.</summary>
    [ObservableProperty]
    private string _lastExportStatus = string.Empty;

    /// <summary>True while an export is in progress.</summary>
    [ObservableProperty]
    private bool _isExporting;

    /// <summary>True after the most recent export succeeded. Drives Open-Folder button visibility.</summary>
    [ObservableProperty]
    private bool _lastExportSucceeded;

    /// <summary>Absolute path of the most recently exported file. Used by OpenOutputDirectoryCommand.</summary>
    [ObservableProperty]
    private string _lastExportedFilePath = string.Empty;

    /// <summary>
    /// File dialog service for showing the save dialog.
    /// Must be set by the hosting ViewModel before commands are used.
    /// </summary>
    public IFileDialogService? FileDialogService { get; set; }

    /// <summary>Callback to update the main status bar text.</summary>
    public Action<string>? UpdateStatus { get; set; }

    /// <summary>Initializes a new instance of <see cref="PhotonTorchExportViewModel"/>.</summary>
    /// <param name="exporter">Core PhotonTorch script generator.</param>
    /// <param name="canvas">Design canvas providing components and connections.</param>
    /// <param name="errorConsole">Optional service for surfacing best-effort failures (e.g. auto-open folder)
    /// to the bottom-panel error console without overwriting the user-facing status message.</param>
    /// <param name="urlLauncher">Optional launcher for opening URLs and file-system paths; defaults to
    /// <see cref="PlatformShellLauncher.CreateDefault"/> when not supplied.</param>
    public PhotonTorchExportViewModel(
        PhotonTorchExporter exporter,
        DesignCanvasViewModel canvas,
        ErrorConsoleService? errorConsole = null,
        IUrlLauncher? urlLauncher = null)
    {
        _exporter = exporter;
        _canvas = canvas;
        _errorConsole = errorConsole;
        _urlLauncher = urlLauncher ?? PlatformShellLauncher.CreateDefault();
    }

    /// <summary>
    /// Exports the current design to a PhotonTorch Python script.
    /// Shows a save dialog and writes the generated script to the chosen path.
    /// </summary>
    [RelayCommand]
    public async Task ExportAsync()
    {
        if (FileDialogService == null)
        {
            LastExportStatus = "Export not available";
            return;
        }

        if (_canvas.Components.Count == 0)
        {
            LastExportStatus = "Nothing to export — add some components first";
            return;
        }

        var filePath = await FileDialogService.ShowSaveFileDialogAsync(
            "Export to PhotonTorch",
            "py",
            "Python Files|*.py|All Files|*.*");

        if (filePath == null)
            return;

        IsExporting = true;
        LastExportStatus = "Generating script…";
        // Reset success state from any previous run so a failure here doesn't leave
        // the Open-Folder button pointing at last successful export's path.
        LastExportSucceeded = false;
        LastExportedFilePath = string.Empty;

        try
        {
            var options = new PhotonTorchExporter.ExportOptions
            {
                WavelengthNm = WavelengthNm,
                Mode = UseTimeDomain
                    ? PhotonTorchExporter.SimulationMode.TimeDomain
                    : PhotonTorchExporter.SimulationMode.SteadyState,
                BitRateGbps = BitRateGbps,
                TimeDomainSteps = TimeDomainSteps,
            };

            var components = _canvas.Components.Select(vm => vm.Component).ToList();
            var connections = _canvas.Connections.Select(vm => vm.Connection).ToList();

            var script = _exporter.Export(components, connections, options);
            await File.WriteAllTextAsync(filePath, script);

            LastExportedFilePath = filePath;
            LastExportSucceeded = true;
            LastExportStatus = $"Exported to {Path.GetFileName(filePath)}";
            UpdateStatus?.Invoke($"PhotonTorch script saved: {Path.GetFileName(filePath)}");
        }
        catch (InvalidOperationException ex)
        {
            // Thrown by PhotonTorchExporter for design-level issues: unmapped
            // component types, missing pin data, fully-connected design, …
            LastExportStatus = $"Export failed: {ex.Message}";
            UpdateStatus?.Invoke($"PhotonTorch export failed: {ex.Message}");
        }
        catch (IOException ex)
        {
            // File write failed (permissions, disk full, locked target).
            LastExportStatus = $"Could not write file: {ex.Message}";
            UpdateStatus?.Invoke($"PhotonTorch export failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            LastExportStatus = $"Access denied: {ex.Message}";
            UpdateStatus?.Invoke($"PhotonTorch export failed: {ex.Message}");
        }
        finally
        {
            IsExporting = false;
        }
    }

    /// <summary>
    /// Opens the directory containing the most recent export in the system file manager.
    /// Bound to a button in the dialog that becomes visible after a successful export.
    /// </summary>
    [RelayCommand]
    public void OpenOutputDirectory()
    {
        if (!LastExportSucceeded || string.IsNullOrEmpty(LastExportedFilePath))
            return;
        OpenContainingDirectoryInFileManager(LastExportedFilePath);
    }

    // Best effort: a failure to open the folder must not look like an export failure
    // (the export already succeeded and the user-facing StatusText reflects that). Log to the
    // error console instead so the failure is still discoverable for troubleshooting.
    private void OpenContainingDirectoryInFileManager(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;
        try
        {
            _urlLauncher.OpenFileOrDirectory(directory);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _errorConsole?.LogWarning($"Could not auto-open output folder '{directory}': {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            _errorConsole?.LogWarning($"Could not auto-open output folder '{directory}': {ex.Message}");
        }
    }

    /// <summary>
    /// Generates the PhotonTorch script and returns it as a string without saving.
    /// Useful for preview or programmatic access.
    /// </summary>
    /// <param name="options">Export options; uses current ViewModel state if null.</param>
    /// <returns>The generated Python script content.</returns>
    public string GenerateScript(PhotonTorchExporter.ExportOptions? options = null)
    {
        options ??= new PhotonTorchExporter.ExportOptions
        {
            WavelengthNm = WavelengthNm,
            Mode = UseTimeDomain
                ? PhotonTorchExporter.SimulationMode.TimeDomain
                : PhotonTorchExporter.SimulationMode.SteadyState,
            BitRateGbps = BitRateGbps,
            TimeDomainSteps = TimeDomainSteps,
        };

        var components = _canvas.Components.Select(vm => vm.Component).ToList();
        var connections = _canvas.Connections.Select(vm => vm.Connection).ToList();

        return _exporter.Export(components, connections, options);
    }
}
