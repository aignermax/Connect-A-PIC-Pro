using System.Text.RegularExpressions;
using CAP_Core.Components.Core;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Import.Gds;

namespace CAP.Avalonia.Services.GdsImport;

/// <summary>
/// Maps a <see cref="GdsCellDraft"/> (pure-data output of the GDS hierarchy
/// importer) to a persistable <see cref="PdkComponentDraft"/>, mirroring the
/// shape <c>CustomComponentDraftFactory</c> establishes for black-box custom
/// components: <see cref="PdkComponentDraft.SMatrix"/> stays null (no simulation
/// model — the component simulates as a lossless pass-through), and geometry is
/// carried by <see cref="PdkComponentDraft.RawCode"/> plus the outline polygons.
/// </summary>
public static class GdsCellDraftMapper
{
    /// <summary>Library category imported GDS components are grouped under.</summary>
    public const string ImportCategory = "GDS Import";

    /// <summary>
    /// Converts <paramref name="draft"/> to a PDK component draft. Pin names are
    /// normalized first (<see cref="GdsPinNameNormalizer"/>): blank label pins
    /// (legal GDS) become <c>pin_N</c> and duplicate names get <c>_2</c>,
    /// <c>_3</c>, … suffixes — the PDK loader rejects blank pin names on load
    /// (one saved blank pin would poison the whole user-PDK file), and
    /// connections resolve pins by name, so duplicates would silently mis-wire.
    /// </summary>
    /// <param name="draft">The imported cell draft (app-space pins/outlines).</param>
    /// <param name="gdsFilePathForRawCode">
    /// Path substituted for the <c>{GdsFileName}</c> token in
    /// <see cref="GdsCellDraft.RawCode"/>. Must be ABSOLUTE: the raw-code
    /// executor (<c>NazcaComponentPreviewService.RenderRawCodeAsync</c>) writes
    /// the snippet to a temp .py file and runs Python with the preview script's
    /// directory as the working directory, so a bare file name would resolve
    /// against neither the user-PDK folder nor the copied .gds. The caller
    /// (GdsImportService) passes the path of the .gds copy next to the user-PDK
    /// JSON. Backslashes and quotes are escaped for the Python string literal.
    /// </param>
    /// <param name="warnings">
    /// Optional warning sink (the import's warning list) receiving one entry per
    /// renamed pin; null discards the warnings (renames still happen).
    /// </param>
    public static PdkComponentDraft Map(GdsCellDraft draft, string gdsFilePathForRawCode, List<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrEmpty(gdsFilePathForRawCode);

        var pins = GdsPinNameNormalizer.Normalize(
            draft.Pins, $"Cell '{draft.CellName}'", warnings ?? new List<string>());

        return new PdkComponentDraft
        {
            Name = SanitizeComponentName(draft.CellName),
            Category = ImportCategory,
            WidthMicrometers = draft.WidthUm,
            HeightMicrometers = draft.HeightUm,
            Pins = pins.Select(p => new PhysicalPinDraft
            {
                Name = p.Name,
                OffsetXMicrometers = p.XUm,
                OffsetYMicrometers = p.YUm,
                AngleDegrees = p.AngleDegrees,
                // Electrical only when the kind is proven (metal-route inference,
                // issue #682): null/unknown stays absent, which the PDK loader
                // reads as the optical default — never guessed.
                PinKind = p.IsElectrical == true
                    ? CAP_Core.Components.PinKinds.PinKindHelper.ElectricalKindName
                    : null,
            }).ToList(),
            SMatrix = null,
            RawCode = SubstituteGdsFileName(draft.RawCode, gdsFilePathForRawCode),
            RawCodeBackend = draft.RawCodeBackend,
            OutlinePolygons = MapOutlines(draft.Outlines),
        };
    }

    /// <summary>
    /// Turns a GDS cell name into a valid component identifier: every character
    /// outside <c>[A-Za-z0-9_.-]</c> (spaces, quotes, …) becomes an underscore.
    /// Deterministic; falls back to <c>gds_cell</c> for empty/all-invalid names.
    /// </summary>
    public static string SanitizeComponentName(string cellName)
    {
        var sanitized = Regex.Replace(cellName ?? string.Empty, @"[^A-Za-z0-9_.\-]", "_");
        return sanitized.Length == 0 ? "gds_cell" : sanitized;
    }

    /// <summary>
    /// Replaces every <c>{GdsFileName}</c> token with the given path, escaped for
    /// the double-quoted Python string literal the token sits inside.
    /// </summary>
    internal static string SubstituteGdsFileName(string rawCode, string gdsFilePath)
    {
        var escaped = gdsFilePath.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return rawCode.Replace(GdsHierarchyImporter.GdsFileNameToken, escaped);
    }

    /// <summary>Field-by-field outline mapping; null when the draft has no outlines.</summary>
    private static List<OutlinePolygon>? MapOutlines(IReadOnlyList<GdsOutlinePolygon> outlines) =>
        outlines.Count == 0
            ? null
            : outlines.Select(o => new OutlinePolygon
            {
                Layer = o.Layer,
                DataType = o.DataType,
                Points = o.Points.Select(p => new OutlinePoint(p.X, p.Y)).ToList(),
            }).ToList();
}
