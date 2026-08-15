using System.Windows.Input;
using CAP.Avalonia.Services.GdsImport;
using CAP_DataAccess.Import.Gds;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.GdsImport;

/// <summary>
/// One row in the import dialog's guessed-pins list. Wraps a
/// <see cref="GdsPinSuggestion"/> with a remove command so the user can delete
/// false positives before placing the import.
/// </summary>
public sealed class GdsImportPinSuggestion
{
    /// <summary>Initializes a new suggestion row.</summary>
    /// <param name="suggestion">The underlying detected heuristic pin.</param>
    /// <param name="onRemove">Callback invoked by <see cref="RemoveCommand"/>.</param>
    public GdsImportPinSuggestion(GdsPinSuggestion suggestion, Action<GdsImportPinSuggestion> onRemove)
    {
        Suggestion = suggestion;
        OnRemove = onRemove;
        RemoveCommand = new RelayCommand(() => OnRemove(this));
    }

    /// <summary>The backing detected pin, including source and geometry.</summary>
    public GdsPinSuggestion Suggestion { get; }

    /// <summary>The GDS cell that owns the pin.</summary>
    public string CellName => Suggestion.CellName;

    /// <summary>The final, normalized pin name.</summary>
    public string PinName => Suggestion.Pin.Name;

    /// <summary>App-space X in micrometers.</summary>
    public double XUm => Suggestion.Pin.XUm;

    /// <summary>App-space Y in micrometers.</summary>
    public double YUm => Suggestion.Pin.YUm;

    /// <summary>Outward direction in degrees (app convention).</summary>
    public double AngleDegrees => Suggestion.Pin.AngleDegrees;

    /// <summary>Pin width in micrometers.</summary>
    public double WidthUm => Suggestion.Pin.WidthUm;

    /// <summary>
    /// True when the pin was inferred by the edge heuristic rather than read
    /// from a label or arrow marker. These are the guesses the user can remove.
    /// </summary>
    public bool IsGuessed => Suggestion.Pin.Source == DetectedPinSource.EdgeHeuristic;

    /// <summary>Removes this suggestion from the dialog list.</summary>
    public ICommand RemoveCommand { get; }

    private Action<GdsImportPinSuggestion> OnRemove { get; }
}
