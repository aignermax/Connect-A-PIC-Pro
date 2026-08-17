using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// Assembly half of <see cref="LogicPanelViewModel"/>: runs the
/// <see cref="CAP_Core.Analysis.LogicAnalysis.LogicNetworkAssembler"/> asynchronously
/// over the current design with cancellation and maps the result (or any failure)
/// onto the panel's display state.
/// </summary>
public partial class LogicPanelViewModel
{
    /// <summary>Assembles the logic network of the current design at the active wavelength.</summary>
    [RelayCommand]
    private async Task BuildNetwork()
    {
        if (IsProcessing || _canvas == null)
            return;

        _buildCts = new CancellationTokenSource();
        IsProcessing = true;
        StatusText = Translate("Analysis.LogicPanel.Running");
        try
        {
            var components = _canvas.Components.Select(c => c.Component).ToList();
            var connections = _canvas.Connections.Select(c => c.Connection).ToList();
            var wavelengthNm = ResolveWavelengthNm();
            WavelengthText = string.Format(Translate("LogicPanel.Wavelength"), wavelengthNm);

            var network = await _assembler.AssembleAsync(
                components, connections, wavelengthNm, _buildCts.Token);
            ShowNetwork(network);
            StatusText = string.Format(
                Translate("Analysis.LogicPanel.Complete"), Inputs.Count, Outputs.Count);
        }
        catch (OperationCanceledException)
        {
            ClearNetwork();
            StatusText = Translate("Analysis.LogicPanel.Cancelled");
        }
        catch (Exception ex)
        {
            // No gate in the design (InvalidOperationException), wiring violations
            // naming the pins (ArgumentException), or an unexpected simulation
            // failure: a readable message, never a crash or a stale "running" state.
            ClearNetwork();
            StatusText = string.Format(Translate("Analysis.LogicPanel.Failed"), ex.Message);
        }
        finally
        {
            IsProcessing = false;
            _buildCts?.Dispose();
            _buildCts = null;
        }
    }

    /// <summary>Cancels the running assembly.</summary>
    [RelayCommand]
    private void Cancel() => _buildCts?.Cancel();
}
