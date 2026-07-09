using Avalonia.Controls;
using CAP_Core;
using CAP.Avalonia.ViewModels;

namespace CAP.Avalonia.Views.Dialogs;

/// <summary>
/// Wires the <c>ShowOptionsDialogAsync</c> callbacks for export formats that need a config dialog.
/// Each callback is wrapped in a try/catch so dialog construction or ShowDialog failures
/// surface to the user via <see cref="MainViewModel.StatusText"/> instead of being swallowed
/// by AsyncRelayCommand's exception capture, and are persisted to the error console
/// so they remain discoverable after StatusText is overwritten by subsequent updates.
/// </summary>
public static class ExportDialogWiring
{
    /// <summary>Wires all export-dialog callbacks on the given ViewModel.</summary>
    /// <param name="vm">MainViewModel exposing the format adapters.</param>
    /// <param name="owner">Owner window used as parent for the modal dialogs.</param>
    /// <param name="errorConsole">Optional error-console service used to persist dialog failures
    /// for after-the-fact debugging (StatusText is ephemeral).</param>
    public static void Wire(MainViewModel vm, Window owner, ErrorConsoleService? errorConsole = null)
    {
        vm.PhotonTorchExportFormat.ShowOptionsDialogAsync = () =>
            ShowSafelyAsync(vm, owner, errorConsole, "PhotonTorch",
                () => new PhotonTorchExportDialog { DataContext = vm.FileOperations.PhotonTorchExport });

        vm.VerilogAExportFormat.ShowOptionsDialogAsync = () =>
            ShowSafelyAsync(vm, owner, errorConsole, "Verilog-A",
                () => new VerilogAExportDialog { DataContext = vm.VerilogAExportFormat.OptionsViewModel });

        vm.GdsFactoryExportFormat.ShowOptionsDialogAsync = () =>
        {
            // Recompute the stub-fallback list against the current canvas on every open.
            vm.GdsFactoryExport.RefreshUnmappedComponents();
            return ShowSafelyAsync(vm, owner, errorConsole, "gdsfactory",
                () => new GdsFactoryExportDialog { DataContext = vm.GdsFactoryExport });
        };
    }

    private static async Task ShowSafelyAsync(MainViewModel vm, Window owner, ErrorConsoleService? errorConsole, string formatName, Func<Window> dialogFactory)
    {
        try
        {
            await dialogFactory().ShowDialog(owner);
        }
        catch (Exception ex)
        {
            // Status bar is ephemeral; also persist to the error console with full type info
            // so the failure is still inspectable after subsequent status updates overwrite the bar.
            vm.StatusText = $"Failed to open {formatName} export dialog: {ex.GetType().Name}: {ex.Message}";
            errorConsole?.LogError($"Failed to open {formatName} export dialog: {ex.Message}", ex);
        }
    }
}
