using System.Collections.ObjectModel;
using System.Globalization;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.Services.Localization;
using CAP_DataAccess.Import.Gds.LayerCensus;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.GdsImport;

/// <summary>
/// The layer-census and layer-suggestion half of
/// <see cref="GdsImportDialogViewModel"/>: shows the file's (layer, datatype)
/// facts next to the layer fields and renders assignment suggestions as
/// explicitly labeled chips. Census = facts, suggestions = labeled guesses.
/// Confident suggestions (foundry-table / text-evidence) are auto-applied to
/// the fields; undecidable ones stay manual. Every field value remains
/// user-editable, and clicking a chip toggles its pair in/out.
/// </summary>
public partial class GdsImportDialogViewModel
{
    private IReadOnlyList<GdsLayerCensusEntry> _layerCensus = Array.Empty<GdsLayerCensusEntry>();

    /// <summary>The file's layer census, one clickable row per (layer, datatype) pair.</summary>
    public ObservableCollection<GdsLayerCensusRow> CensusRows { get; } = new();

    /// <summary>Suggestion chips for the currently selected top cell.</summary>
    public ObservableCollection<GdsLayerSuggestionChip> SuggestionChips { get; } = new();

    /// <summary>True when the census section has rows to show.</summary>
    public bool HasCensus => CensusRows.Count > 0;

    /// <summary>True when the suggestion section has chips to show.</summary>
    public bool HasSuggestions => SuggestionChips.Count > 0;

    /// <summary>
    /// The layer field a census-row click appends to — the field that last had
    /// focus (set by the view's GotFocus handlers), defaulting to port labels.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CensusHintText))]
    private GdsLayerFieldTarget _activeLayerField = GdsLayerFieldTarget.PortLabels;

    /// <summary>Hint under the census header naming the field a click appends to.</summary>
    public string CensusHintText => string.Format(
        LocalizationService.Instance.Translate("GdsImport.CensusHint"),
        LocalizationService.Instance.Translate(ActiveLayerField switch
        {
            GdsLayerFieldTarget.Waveguide => "GdsImport.WaveguideLayersLabel",
            GdsLayerFieldTarget.Metal => "GdsImport.MetalLayersLabel",
            _ => "GdsImport.PortLayersLabel",
        }).TrimEnd(':'));

    /// <summary>Fills the census rows after a successful analysis and rebuilds the suggestions.</summary>
    private void PopulateCensus(IReadOnlyList<GdsLayerCensusEntry> census)
    {
        _layerCensus = census;
        CensusRows.Clear();
        foreach (var entry in census)
            CensusRows.Add(new GdsLayerCensusRow(entry));
        OnPropertyChanged(nameof(HasCensus));
        RebuildSuggestions();
    }

    /// <summary>Appends the clicked census row's pair to the last-focused layer field.</summary>
    [RelayCommand]
    private void AppendCensusRow(GdsLayerCensusRow row) =>
        AppendLayerPair(ActiveLayerField, row.Entry.Layer, row.Entry.Datatype);

    /// <summary>
    /// Toggles a suggestion chip: accepting appends its pair to the chip's
    /// target field, clicking an accepted chip again removes the pair.
    /// "Routing, kind unknown" chips are not acceptable — the user assigns
    /// those layers deliberately via a census-row click.
    /// </summary>
    [RelayCommand]
    private void AcceptSuggestion(GdsLayerSuggestionChip chip)
    {
        if (!chip.IsAcceptable)
            return;
        if (chip.IsAccepted)
            RemoveLayerPair(chip.TargetField, chip.Suggestion.Layer, chip.Suggestion.Datatype);
        else
            AppendLayerPair(chip.TargetField, chip.Suggestion.Layer, chip.Suggestion.Datatype);
    }

    /// <summary>Accepts every acceptable, not-yet-accepted suggestion chip in one click.</summary>
    [RelayCommand(CanExecute = nameof(CanAcceptAllSuggestions))]
    private void AcceptAllSuggestions()
    {
        foreach (var chip in SuggestionChips)
            if (chip.IsAcceptable && !chip.IsAccepted)
                AppendLayerPair(chip.TargetField, chip.Suggestion.Layer, chip.Suggestion.Datatype);
    }

    private bool CanAcceptAllSuggestions() =>
        SuggestionChips.Any(c => c.IsAcceptable && !c.IsAccepted);

    /// <summary>
    /// Suggestions depend on the selected top cell (its drawn routes feed the
    /// route-candidate heuristic), so they are rebuilt on every selection change.
    /// </summary>
    partial void OnSelectedTopCellChanged(GdsTopCellSummary? value) => RebuildSuggestions();

    partial void OnPortLayersTextChanged(string value) => RefreshAcceptedStates();

    partial void OnWaveguideLayersTextChanged(string value) => RefreshAcceptedStates();

    partial void OnMetalLayersTextChanged(string value) => RefreshAcceptedStates();

    private void RebuildSuggestions()
    {
        SuggestionChips.Clear();
        if (_analyzedLibrary is not null && SelectedTopCell is not null && _layerCensus.Count > 0)
        {
            var suggestions = GdsLayerSuggestionEngine.Build(
                _analyzedLibrary, SelectedTopCell.CellName, _layerCensus);
            foreach (var suggestion in suggestions)
                SuggestionChips.Add(new GdsLayerSuggestionChip(suggestion));
        }
        // Auto-apply only what the engine can decide reliably: high-confidence
        // (text-backed port-label / port-attachment waveguide) suggestions
        // write their pairs into the fields directly. Metal/waveguide table
        // claims (medium — layer numbers collide across foundries) and
        // "routing, kind unknown" (low) wait for a click: a silent wrong
        // metal/waveguide call misroutes the import. Appends are idempotent; a
        // pair the user removed by hand is only re-applied on the next rebuild
        // (re-analysis or top-cell change).
        foreach (var chip in SuggestionChips)
        {
            if (!chip.IsAcceptable || chip.Suggestion.Confidence != GdsSuggestionConfidence.High)
                continue;
            if (chip.Suggestion.Role == GdsLayerRole.Waveguide)
                // Attachment-proven optical: the same pair in the metal field is
                // a wrong default/convention entry — pull it, otherwise the
                // layer's routes import as electrical.
                RemoveLayerPair(GdsLayerFieldTarget.Metal, chip.Suggestion.Layer, chip.Suggestion.Datatype);
            AppendLayerPair(chip.TargetField, chip.Suggestion.Layer, chip.Suggestion.Datatype);
        }
        OnPropertyChanged(nameof(HasSuggestions));
        RefreshAcceptedStates();
    }

    /// <summary>
    /// Appends "layer,datatype" to the target field unless the pair is already
    /// listed there (repeated clicks stay idempotent). A malformed field text is
    /// left untouched except for the appended pair — validation happens on
    /// import, with the existing syntax error message.
    /// </summary>
    private void AppendLayerPair(GdsLayerFieldTarget target, int layer, int datatype)
    {
        var current = GetFieldText(target);
        var existing = ParseLayerPairs(current);
        if (existing is not null && existing.Contains((layer, datatype)))
            return;

        var pairText = string.Format(CultureInfo.InvariantCulture, "{0},{1}", layer, datatype);
        var trimmed = current.Trim().TrimEnd(';').TrimEnd();
        SetFieldText(target, trimmed.Length == 0 ? pairText : $"{trimmed}; {pairText}");
    }

    /// <summary>
    /// Removes "layer,datatype" from the target field (all occurrences — a
    /// hand-edited field may list it twice) and normalizes the remaining text.
    /// A malformed field text is left untouched: there is nothing reliable to
    /// remove from it.
    /// </summary>
    private void RemoveLayerPair(GdsLayerFieldTarget target, int layer, int datatype)
    {
        var existing = ParseLayerPairs(GetFieldText(target));
        if (existing is null)
            return;
        if (existing.RemoveAll(p => p.Layer == layer && p.Datatype == datatype) == 0)
            return;
        SetFieldText(target, string.Join("; ", existing.Select(p =>
            string.Format(CultureInfo.InvariantCulture, "{0},{1}", p.Layer, p.Datatype))));
    }

    /// <summary>An accepted chip shows a checkmark while its pair is present in its target field.</summary>
    private void RefreshAcceptedStates()
    {
        foreach (var chip in SuggestionChips)
        {
            var pairs = ParseLayerPairs(GetFieldText(chip.TargetField));
            chip.IsAccepted = pairs?.Contains((chip.Suggestion.Layer, chip.Suggestion.Datatype)) == true;
        }
        AcceptAllSuggestionsCommand.NotifyCanExecuteChanged();
    }

    private string GetFieldText(GdsLayerFieldTarget target) => target switch
    {
        GdsLayerFieldTarget.Waveguide => WaveguideLayersText,
        GdsLayerFieldTarget.Metal => MetalLayersText,
        _ => PortLayersText,
    };

    private void SetFieldText(GdsLayerFieldTarget target, string value)
    {
        switch (target)
        {
            case GdsLayerFieldTarget.Waveguide: WaveguideLayersText = value; break;
            case GdsLayerFieldTarget.Metal: MetalLayersText = value; break;
            default: PortLayersText = value; break;
        }
    }
}
