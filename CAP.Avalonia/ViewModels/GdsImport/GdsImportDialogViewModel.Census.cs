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
/// explicitly labeled chips. Census = facts, suggestions = labeled guesses,
/// fields = user decision — nothing is written into a field without a click.
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

    /// <summary>Accepts a suggestion chip: appends its pair to the chip's target field.</summary>
    [RelayCommand]
    private void AcceptSuggestion(GdsLayerSuggestionChip chip) =>
        AppendLayerPair(chip.TargetField, chip.Suggestion.Layer, chip.Suggestion.Datatype);

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

    /// <summary>An accepted chip shows a checkmark while its pair is present in its target field.</summary>
    private void RefreshAcceptedStates()
    {
        foreach (var chip in SuggestionChips)
        {
            var pairs = ParseLayerPairs(GetFieldText(chip.TargetField));
            chip.IsAccepted = pairs?.Contains((chip.Suggestion.Layer, chip.Suggestion.Datatype)) == true;
        }
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
