using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Export.Formats;

/// <summary>
/// Export format for the gdsfactory YAML netlist (issue #687): the design's circuit
/// topology (instances, placements, connections, ports) in the gdsfactory netlist
/// interchange shape — for SAX and gdsfactory netlist tooling. It is a topology/circuit
/// netlist, not a drawn-layout round-trip (see <c>GdsFactoryYamlNetlistWriter</c>).
/// </summary>
public class NetlistExportFormat : IExportFormat
{
    private readonly IAsyncRelayCommand _exportCommand;

    /// <inheritdoc/>
    public string Name => "Netlist (YAML)";

    /// <inheritdoc/>
    public string Icon => "🕸️";

    /// <inheritdoc/>
    public string Description => "Export the circuit topology as a gdsfactory YAML netlist";

    /// <inheritdoc/>
    public string Background => "#3d4d4d";

    /// <inheritdoc/>
    public IAsyncRelayCommand ExportCommand => _exportCommand;

    /// <summary>
    /// Initializes with the save command from <c>NetlistViewModel.SaveYamlCommand</c>.
    /// </summary>
    /// <param name="exportCommand">The async command that performs the netlist save flow.</param>
    public NetlistExportFormat(IAsyncRelayCommand exportCommand)
    {
        _exportCommand = exportCommand;
    }
}
