namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// The foreign-file metal-defaults note of
/// <see cref="GdsHierarchyImportSession"/>: when a file without the Lunima
/// export sentinel leaves the metal layers on AUTO, the exporter defaults are
/// NOT applied — this partial tells the user so when it matters.
/// </summary>
internal sealed partial class GdsHierarchyImportSession
{
    /// <summary>True when AUTO metal layers resolved to NONE (foreign file, no explicit mapping).</summary>
    private readonly bool _metalDefaultsAutoDisabled;
    private bool _metalDefaultsNoteEmitted;

    /// <summary>
    /// One info note when a FOREIGN file (no Lunima export sentinel, metal
    /// layers left on AUTO → none) carries top-cell geometry on the layer
    /// numbers our exporters use for metal: the user should decide whether
    /// those are metal here and map them explicitly. Silent for foreign files
    /// without such geometry — the note would be pure noise.
    /// </summary>
    private void NoteSkippedMetalDefaults(IReadOnlyList<GdsOutlinePolygon> residualPolygons)
    {
        if (!_metalDefaultsAutoDisabled || _metalDefaultsNoteEmitted)
            return;
        if (!residualPolygons.Any(p =>
            GdsHierarchyImportOptions.LunimaMetalRouteLayers.Contains((p.Layer, p.DataType))))
        {
            return;
        }
        _metalDefaultsNoteEmitted = true;
        Infos.Add(
            "Foreign GDS (no Lunima export marker): geometry on (11,0)/(12,0) was NOT treated "
            + "as metal routing — foundry layer tables assign these numbers differently. If this "
            + "file's electrical routing lives on those (or other) layers, set its metal layers "
            + "in the import options.");
    }
}
