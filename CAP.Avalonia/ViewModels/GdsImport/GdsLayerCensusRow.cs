using CAP.Avalonia.Services.Localization;
using CAP_DataAccess.Import.Gds.LayerCensus;

namespace CAP.Avalonia.ViewModels.GdsImport;

/// <summary>
/// One clickable row of the import dialog's layer census: the (layer, datatype)
/// pair plus its element counts, formatted for display. Clicking a row appends
/// the pair to the layer field that last had focus.
/// </summary>
public sealed class GdsLayerCensusRow
{
    /// <summary>Display cap for the "texts in: …" cell list.</summary>
    private const int MaxCellNamesShown = 4;

    /// <summary>The census facts behind this row.</summary>
    public GdsLayerCensusEntry Entry { get; }

    /// <summary>The pair as shown and as appended to a field, e.g. <c>(1,10)</c>.</summary>
    public string PairText { get; }

    /// <summary>Compact counts line, e.g. <c>3 polygons · 2 paths · 12 texts</c>.</summary>
    public string CountsText { get; }

    /// <summary>Which cells carry the texts (capped list); empty without texts.</summary>
    public string TextCellsText { get; }

    /// <summary>True when <see cref="TextCellsText"/> should be shown.</summary>
    public bool HasTextCells => TextCellsText.Length > 0;

    /// <summary>Initializes a row from one census entry.</summary>
    public GdsLayerCensusRow(GdsLayerCensusEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        PairText = $"({entry.Layer},{entry.Datatype})";
        CountsText = string.Format(
            LocalizationService.Instance.Translate("GdsImport.CensusCountsFormat"),
            entry.PolygonCount, entry.PathCount, entry.TextCount);
        TextCellsText = entry.TextCount == 0
            ? string.Empty
            : string.Format(
                LocalizationService.Instance.Translate("GdsImport.CensusTextCellsFormat"),
                FormatCellList(entry.TextCellNames));
    }

    private static string FormatCellList(IReadOnlyList<string> cellNames)
    {
        var shown = string.Join(", ", cellNames.Take(MaxCellNamesShown));
        return cellNames.Count > MaxCellNamesShown
            ? $"{shown} (+{cellNames.Count - MaxCellNamesShown})"
            : shown;
    }
}
