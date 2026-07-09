using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core;
using CAP_Core.Export.Netlist;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Export.Netlist;

/// <summary>
/// ViewModel for the Netlist panel (issue #687): derives a gdsfactory YAML netlist
/// from the current design, shows it read-only, and offers copy-to-clipboard and
/// save-to-file. Persona Mirko's bridge into the gdsfactory/SAX ecosystem.
/// </summary>
public partial class NetlistViewModel : ObservableObject
{
    private readonly NetlistDeriver _deriver = new();
    private readonly GdsFactoryYamlNetlistWriter _writer = new();
    private readonly ErrorConsoleService? _errorConsole;
    private DesignCanvasViewModel? _canvas;

    /// <summary>The generated YAML netlist text shown in the panel.</summary>
    [ObservableProperty]
    private string _netlistYaml = "";

    /// <summary>Short user-facing status line (counts, errors, save confirmation).</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>True when a netlist has been generated (drives the preview visibility).</summary>
    public bool HasNetlist => NetlistYaml.Length > 0;

    partial void OnNetlistYamlChanged(string value) => OnPropertyChanged(nameof(HasNetlist));

    /// <summary>File dialog service for the save flow; wired by the View code-behind.</summary>
    public Services.IFileDialogService? FileDialogService { get; set; }

    /// <summary>
    /// Clipboard callback; wired by the View code-behind because clipboard access
    /// requires a TopLevel reference (same pattern as RoutingDiagnostics).
    /// </summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>Initializes a new instance of <see cref="NetlistViewModel"/>.</summary>
    /// <param name="errorConsole">Optional service for error logging.</param>
    public NetlistViewModel(ErrorConsoleService? errorConsole = null)
    {
        _errorConsole = errorConsole;
    }

    /// <summary>Configures the panel with the design canvas to derive netlists from.</summary>
    public void Configure(DesignCanvasViewModel? canvas) => _canvas = canvas;

    /// <summary>Regenerates the YAML netlist from the current design.</summary>
    [RelayCommand]
    private void Refresh() => TryGenerate();

    /// <summary>Copies the current netlist YAML to the system clipboard.</summary>
    [RelayCommand]
    private async Task CopyYaml()
    {
        if (!TryGenerate()) return;
        if (CopyToClipboard == null)
        {
            StatusText = "Clipboard not available";
            return;
        }
        await CopyToClipboard(NetlistYaml);
        StatusText = "Netlist copied to clipboard";
    }

    /// <summary>Saves the current netlist YAML to a .yml file chosen by the user.</summary>
    [RelayCommand]
    private async Task SaveYaml()
    {
        if (!TryGenerate()) return;
        if (FileDialogService == null)
        {
            StatusText = "Export not available";
            return;
        }

        var filePath = await FileDialogService.ShowSaveFileDialogAsync(
            "Export netlist (gdsfactory YAML)",
            "yml",
            "YAML Netlist|*.yml;*.yaml|All Files|*.*");
        if (filePath == null)
        {
            StatusText = "Export cancelled";
            return;
        }

        try
        {
            await File.WriteAllTextAsync(filePath, NetlistYaml);
            StatusText = $"Exported netlist: {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Failed to export netlist: {ex.Message}", ex);
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Derives and serialises the netlist; false when there is nothing to export or
    /// derivation failed (status text explains why in both cases).
    /// </summary>
    private bool TryGenerate()
    {
        if (_canvas == null || _canvas.Components.Count == 0)
        {
            NetlistYaml = "";
            StatusText = "Nothing to export — add some components first";
            return false;
        }

        try
        {
            var netlist = _deriver.Derive(
                _canvas.Components.Select(vm => vm.Component),
                _canvas.Connections.Select(vm => vm.Connection));
            NetlistYaml = _writer.Write(netlist);
            StatusText = $"{netlist.Instances.Count} instances, " +
                         $"{netlist.Connections.Count} connections, {netlist.Ports.Count} ports";
            return true;
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Netlist derivation failed: {ex.Message}", ex);
            StatusText = $"Netlist failed: {ex.Message}";
            return false;
        }
    }
}
