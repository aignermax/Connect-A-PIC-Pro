using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// ViewModel for the Routing settings page. Wraps the existing
/// <see cref="DesignCanvasViewModel"/> (diagonal routing) and the
/// <see cref="CrossingInsertionCanvasBinder"/> (adaptive crossing insertion,
/// issue #553) so toggling an option applies immediately.
/// </summary>
public partial class RoutingSettingsViewModel : ObservableObject
{
    private readonly CrossingInsertionCanvasBinder? _crossingBinder;

    /// <summary>The canvas whose routing flags this page edits.</summary>
    public DesignCanvasViewModel Canvas { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="RoutingSettingsViewModel"/>.
    /// </summary>
    /// <param name="canvas">The live canvas ViewModel.</param>
    /// <param name="crossingBinder">The canvas' crossing-insertion wiring; its
    /// <see cref="CrossingInsertionCanvasBinder.IsEnabled"/> flag backs the
    /// crossing toggle. Null in tests / headless contexts — the toggle is
    /// then inert.</param>
    public RoutingSettingsViewModel(DesignCanvasViewModel canvas,
                                    CrossingInsertionCanvasBinder? crossingBinder = null)
    {
        Canvas = canvas;
        _crossingBinder = crossingBinder;
        _crossingInsertionEnabled = crossingBinder?.IsEnabled ?? true;
    }

    /// <summary>
    /// When on, routing runs an extra pass that replaces a detouring waveguide with a real
    /// PDK crossing component where that lowers insertion loss (issue #553). Turning it off
    /// keeps classic avoid-only routing — faster on large designs and never inserts crossings.
    /// </summary>
    [ObservableProperty]
    private bool _crossingInsertionEnabled;

    partial void OnCrossingInsertionEnabledChanged(bool value)
    {
        if (_crossingBinder != null)
            _crossingBinder.IsEnabled = value;
    }
}
