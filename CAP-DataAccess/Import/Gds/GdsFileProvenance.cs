namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Detects whether a GDS file was produced by an external (nazca-based)
/// foundry flow rather than by Lunima itself. nazca stamps every cell with a
/// metadata text blob ("cellname: …\nfoundry_pdk: …\nnazca_pdk_version: …");
/// Lunima's own exports never carry these. The import dialog uses this to
/// decide whether the Lunima-specific default layer lists (our own export
/// conventions) may be pre-filled: on a foreign file they are wrong more
/// often than right — one foundry's metal number is another's core etch —
/// so the fields start empty and fill purely from file evidence.
/// </summary>
public static class GdsFileProvenance
{
    /// <summary>True when any cell carries a nazca/foundry metadata stamp.</summary>
    public static bool HasForeignStamps(GdsLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        foreach (var cell in library.Cells.Values)
        {
            foreach (var text in cell.Elements.OfType<GdsText>())
            {
                if (text.Text.Contains("foundry_pdk", StringComparison.Ordinal)
                    || text.Text.Contains("nazca_pdk_version", StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }
}
