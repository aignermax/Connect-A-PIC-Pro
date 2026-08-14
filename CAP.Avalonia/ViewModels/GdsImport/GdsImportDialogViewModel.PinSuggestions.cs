using System.Collections.ObjectModel;
using CAP.Avalonia.Services.GdsImport;
using CAP_DataAccess.Import.Gds;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.GdsImport;

/// <summary>
/// The guessed-pins half of <see cref="GdsImportDialogViewModel"/>. Shows
/// edge-heuristic pins as user-reviewable suggestions and lets the user remove
/// false positives before the import runs. Removed pins are forwarded to the
/// import service as <see cref="GdsGuessedPin"/> exclusions.
/// </summary>
public partial class GdsImportDialogViewModel
{
    /// <summary>Edge-heuristic pins the user can review and delete.</summary>
    public ObservableCollection<GdsImportPinSuggestion> PinSuggestions { get; } = new();

    /// <summary>True when at least one guessed pin is available for review.</summary>
    public bool HasPinSuggestions => PinSuggestions.Count > 0;

    /// <summary>Heuristic pins removed by the user; passed into import options.</summary>
    private readonly List<GdsGuessedPin> _excludedGuessedPins = new();

    /// <summary>Clears the suggestion state when a new analysis starts.</summary>
    private void ClearPinSuggestions()
    {
        PinSuggestions.Clear();
        _excludedGuessedPins.Clear();
        OnPropertyChanged(nameof(HasPinSuggestions));
    }

    /// <summary>
    /// Rebuilds the guessed-pin list for the current top cell and layer
    /// options. Run on top-cell changes and layer-field changes. Invalid layer
    /// syntax simply clears the list — the user sees the syntax error when
    /// trying to import.
    /// </summary>
    private void RebuildPinSuggestions()
    {
        PinSuggestions.Clear();

        if (_analyzedLibrary is null || SelectedTopCell is null)
        {
            OnPropertyChanged(nameof(HasPinSuggestions));
            return;
        }

        if (!TryBuildOptions(out var options, out _))
        {
            OnPropertyChanged(nameof(HasPinSuggestions));
            return;
        }

        var suggestions = GdsPinSuggestionEngine.Build(
            _analyzedLibrary, SelectedTopCell.CellName, options);

        foreach (var suggestion in suggestions)
        {
            if (_excludedGuessedPins.Any(e =>
                e.CellName == suggestion.CellName && e.PinName == suggestion.Pin.Name))
            {
                continue;
            }

            PinSuggestions.Add(
                new GdsImportPinSuggestion(suggestion, RemovePinSuggestion));
        }

        OnPropertyChanged(nameof(HasPinSuggestions));
    }

    /// <summary>Removes a guessed pin from the list and excludes it from import.</summary>
    [RelayCommand]
    private void RemovePinSuggestion(GdsImportPinSuggestion? suggestion)
    {
        if (suggestion is null)
            return;

        _excludedGuessedPins.Add(new GdsGuessedPin(
            suggestion.CellName, suggestion.PinName));
        PinSuggestions.Remove(suggestion);
        OnPropertyChanged(nameof(HasPinSuggestions));
    }
}
