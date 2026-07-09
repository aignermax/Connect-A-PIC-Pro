using CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// ViewModel for general application preferences that do not belong to a
/// dedicated category. Currently surfaces the adaptive crossing-insertion
/// toggle (issue #553); additional general preferences can be added here.
/// </summary>
public partial class GeneralSettingsViewModel : ObservableObject
{
    private readonly CrossingInsertionCanvasBinder? _crossingBinder;

    /// <summary>Parameterless constructor for design-time / fallback use.</summary>
    public GeneralSettingsViewModel()
    {
    }

    /// <summary>Creates the ViewModel bound to the live crossing-insertion binder.</summary>
    /// <param name="crossingBinder">The canvas' crossing-insertion wiring; its
    /// <see cref="CrossingInsertionCanvasBinder.IsEnabled"/> flag backs the toggle.</param>
    public GeneralSettingsViewModel(CrossingInsertionCanvasBinder crossingBinder)
    {
        _crossingBinder = crossingBinder;
        _crossingInsertionEnabled = crossingBinder.IsEnabled;
    }

    /// <summary>
    /// When on, routing runs an extra pass that replaces a detouring waveguide with a real
    /// PDK crossing component where that lowers insertion loss (issue #553). Turning it off
    /// keeps classic avoid-only routing — faster on large designs and never inserts crossings.
    /// </summary>
    [ObservableProperty]
    private bool _crossingInsertionEnabled = true;

    partial void OnCrossingInsertionEnabledChanged(bool value)
    {
        if (_crossingBinder != null)
            _crossingBinder.IsEnabled = value;
    }
}
