using CAP_Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP_Core.Analysis;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using Avalonia.Threading;

namespace CAP.Avalonia.ViewModels.Analysis;

/// <summary>
/// ViewModel for the layout compression feature.
/// Automatically rearranges components to minimize chip area while maintaining connectivity.
/// </summary>
public partial class CompressLayoutViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompressLayoutCommand))]
    private bool _isCompressing;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _resultText = "";

    [ObservableProperty]
    private int _currentIteration;

    [ObservableProperty]
    private int _maxIterations = 100;

    private readonly ErrorConsoleService? _errorConsole;
    private DesignCanvasViewModel? _canvas;
    private readonly LayoutCompressor _compressor = new();

    /// <summary>Initializes a new instance of <see cref="CompressLayoutViewModel"/>.</summary>
    /// <param name="errorConsole">Optional service for error logging.</param>
    public CompressLayoutViewModel(ErrorConsoleService? errorConsole = null)
    {
        _errorConsole = errorConsole;
    }

    /// <summary>
    /// Configures the compressor for the given canvas.
    /// </summary>
    public void Configure(DesignCanvasViewModel canvas)
    {
        _canvas = canvas;
        StatusText = "";
        ResultText = "";
        CompressLayoutCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Runs the layout compression algorithm.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCompressLayout))]
    private async Task CompressLayout()
    {
        if (_canvas == null || _canvas.Components.Count == 0)
        {
            StatusText = LocalizationService.Instance.Translate("Analysis.Compress.NoComponents");
            return;
        }

        if (IsCompressing) return;
        IsCompressing = true;
        StatusText = LocalizationService.Instance.Translate("Analysis.Compress.Running");
        ResultText = "";

        try
        {
            // Calculate original bounding box
            var originalBounds = _compressor.CalculateBoundingBox(
                _canvas.Components.Select(c => c.Component).ToList());

            // Store original positions for undo
            var originalPositions = _canvas.Components
                .ToDictionary(c => c, c => (c.X, c.Y));

            // Reset progress
            CurrentIteration = 0;

            // Run compression with progress updates
            await Task.Run(() =>
            {
                var components = _canvas.Components
                    .Select(c => c.Component)
                    .ToList();

                var connections = _canvas.Connections
                    .Select(c => c.Connection)
                    .ToList();

                _compressor.CompressLayout(components, connections, (iteration, positions) =>
                {
                    // Update UI on the UI thread every 5 iterations
                    Dispatcher.UIThread.Post(() =>
                    {
                        CurrentIteration = iteration;
                        StatusText = string.Format(
                            LocalizationService.Instance.Translate("Analysis.Compress.RunningIteration"),
                            iteration, MaxIterations);

                        // Update component positions for animation
                        foreach (var compVm in _canvas.Components)
                        {
                            compVm.X = compVm.Component.PhysicalX;
                            compVm.Y = compVm.Component.PhysicalY;
                        }
                    });
                });
            });

            // Final UI update
            foreach (var compVm in _canvas.Components)
            {
                compVm.X = compVm.Component.PhysicalX;
                compVm.Y = compVm.Component.PhysicalY;
            }

            // Recalculate waveguide routes
            await _canvas.RecalculateRoutesAsync();

            // Calculate new bounding box
            var newBounds = _compressor.CalculateBoundingBox(
                _canvas.Components.Select(c => c.Component).ToList());

            // Format results
            double areaReduction = ((originalBounds.area - newBounds.area) / originalBounds.area) * 100;

            ResultText = $"Layout compressed successfully:\n" +
                $"Original: {originalBounds.width:F0} × {originalBounds.height:F0} µm " +
                $"({originalBounds.area / 1_000_000:F2} mm²)\n" +
                $"New: {newBounds.width:F0} × {newBounds.height:F0} µm " +
                $"({newBounds.area / 1_000_000:F2} mm²)\n" +
                $"Area reduction: {areaReduction:F1}%";

            StatusText = string.Format(
                LocalizationService.Instance.Translate("Analysis.Compress.Complete"), areaReduction);
            CurrentIteration = MaxIterations;
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Layout compression failed: {ex.Message}", ex);
            StatusText = string.Format(
                LocalizationService.Instance.Translate("Analysis.Compress.Failed"), ex.Message);
            ResultText = "";
        }
        finally
        {
            IsCompressing = false;
        }
    }

    /// <summary>
    /// Checks whether compression can be executed.
    /// </summary>
    private bool CanCompressLayout()
    {
        return _canvas != null &&
               _canvas.Components.Count > 0 &&
               !IsCompressing;
    }
}
