using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP_Core;
using CAP_Core.Export;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.ViewModels.Export;

/// <summary>
/// ViewModel for the Verilog-A / SPICE export options dialog.
/// Allows exporting photonic circuits for co-simulation with electronic circuits.
/// </summary>
public partial class VerilogAExportViewModel : ObservableObject
{
    private readonly VerilogAExporter _exporter;
    private readonly VerilogAFileWriter _fileWriter;
    private readonly DesignCanvasViewModel _canvas;
    private readonly ErrorConsoleService? _errorConsole;
    private readonly IUrlLauncher _urlLauncher;

    [ObservableProperty]
    private int _wavelengthNm = 1550;

    [ObservableProperty]
    private bool _includeTestBench = true;

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _lastExportSucceeded;

    [ObservableProperty]
    private int _lastFileCount;

    /// <summary>Last directory the user exported to. Used by OpenOutputDirectoryCommand.</summary>
    [ObservableProperty]
    private string _lastOutputDirectory = string.Empty;

    /// <summary>
    /// Available wavelength options for S-parameter extraction.
    /// </summary>
    public IReadOnlyList<int> WavelengthOptions { get; } = new[] { 1310, 1550, 1625 };

    /// <summary>
    /// File dialog service for choosing the output location.
    /// Must be set by the hosting ViewModel before commands are used.
    /// </summary>
    public IFileDialogService? FileDialogService { get; set; }

    /// <summary>Initializes the ViewModel with required services.</summary>
    /// <param name="errorConsole">Optional service for surfacing best-effort failures (e.g. auto-open folder)
    /// to the bottom-panel error console without overwriting the user-facing status message.</param>
    /// <param name="urlLauncher">Optional launcher used to open the output folder after export.
    /// Defaults to <see cref="PlatformShellLauncher.CreateDefault"/> when not supplied.</param>
    public VerilogAExportViewModel(
        VerilogAExporter exporter,
        VerilogAFileWriter fileWriter,
        DesignCanvasViewModel canvas,
        ErrorConsoleService? errorConsole = null,
        IUrlLauncher? urlLauncher = null)
    {
        _exporter = exporter;
        _fileWriter = fileWriter;
        _canvas = canvas;
        _errorConsole = errorConsole;
        _urlLauncher = urlLauncher ?? PlatformShellLauncher.CreateDefault();
    }

    /// <summary>
    /// Exports the current design to Verilog-A files.
    /// Prompts the user for a save location, derives the circuit name from the
    /// chosen file name, writes all files into the surrounding directory, then
    /// opens that directory in the system file manager.
    /// </summary>
    [RelayCommand]
    public async Task ExportAsync()
    {
        if (IsExporting) return;
        if (FileDialogService == null)
        {
            StatusText = "Export not available";
            return;
        }

        var components = _canvas.Components
            .Select(vm => vm.Component)
            .Where(c => c != null)
            .ToList();

        if (components.Count == 0)
        {
            StatusText = "No components to export.";
            return;
        }

        var filePath = await FileDialogService.ShowSaveFileDialogAsync(
            "Export Verilog-A circuit",
            "va",
            "Verilog-A Files|*.va|All Files|*.*");

        if (filePath == null) return;

        var outputDirectory = Path.GetDirectoryName(filePath) ?? string.Empty;
        var circuitName = SanitizeCircuitName(Path.GetFileNameWithoutExtension(filePath));

        IsExporting = true;
        StatusText = "Exporting...";
        LastExportSucceeded = false;
        // Reset state from any previous run so a failure here doesn't leave the
        // dialog pointing at stale data (e.g., last successful directory/file count).
        LastFileCount = 0;
        LastOutputDirectory = string.Empty;

        try
        {
            var connections = _canvas.Connections
                .Select(vm => vm.Connection)
                .Where(c => c != null)
                .ToList();

            var options = new VerilogAExportOptions
            {
                WavelengthNm = WavelengthNm,
                CircuitName = circuitName,
                IncludeTestBench = IncludeTestBench
            };

            var result = await Task.Run(() => _exporter.Export(components, connections, options));

            if (!result.Success)
            {
                StatusText = $"✗ Export failed: {result.ErrorMessage}";
                return;
            }

            // Per-circuit subfolder so re-exporting a different circuit
            // never overwrites a previous export (issue #493).
            var circuitOutputDir = Path.Combine(outputDirectory, circuitName);
            await _fileWriter.WriteAsync(result, circuitOutputDir);
            LastFileCount = result.TotalFileCount;
            LastOutputDirectory = circuitOutputDir;
            LastExportSucceeded = true;
            StatusText = $"✓ Exported {result.TotalFileCount} files to {circuitOutputDir}";
        }
        catch (InvalidOperationException ex)
        {
            // Design-level issue surfaced by the exporter (orphan pin, unmapped
            // component type, missing ports, …).
            StatusText = $"✗ Export rejected: {ex.Message}";
        }
        catch (IOException ex)
        {
            StatusText = $"✗ Could not write files: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusText = $"✗ Access denied: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
        }
    }

    /// <summary>
    /// Re-opens the most recent output directory in the system file manager.
    /// </summary>
    [RelayCommand]
    public void OpenOutputDirectory()
    {
        if (string.IsNullOrEmpty(LastOutputDirectory) || !Directory.Exists(LastOutputDirectory))
            return;
        OpenDirectoryInFileManager(LastOutputDirectory);
    }

    // Best effort: a failure to auto-open the folder must not overwrite the success
    // StatusText (the export already succeeded). Log to the error console instead so
    // the failure is still discoverable for troubleshooting.
    private void OpenDirectoryInFileManager(string directory)
    {
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

    private static string SanitizeCircuitName(string name)
    {
        var sanitized = System.Text.RegularExpressions.Regex.Replace(name, @"[^a-zA-Z0-9_]", "_");
        return string.IsNullOrEmpty(sanitized) ? "PhotonicCircuit" : sanitized;
    }
}
