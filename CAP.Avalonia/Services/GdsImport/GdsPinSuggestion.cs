using CAP_DataAccess.Import.Gds;

namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// One edge-heuristic pin offered to the user in the GDS import dialog as a
/// deletable guess. The cell name is the GDS cell the pin belongs to; the
/// <see cref="DetectedPin"/> carries the final, normalized pin name and
/// geometry in the application's coordinate convention.
/// </summary>
public sealed record GdsPinSuggestion(string CellName, DetectedPin Pin);
