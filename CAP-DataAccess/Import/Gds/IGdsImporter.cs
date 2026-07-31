namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Imports GDSII layout files (.gds) into a parsed library model, ready for
/// conversion into the application's component/canvas model.
/// </summary>
public interface IGdsImporter
{
    /// <summary>
    /// Reads the GDS file at <paramref name="gdsPath"/> and returns the flattened
    /// geometry, bounding box and direct instance tree for the chosen top cell.
    /// </summary>
    /// <param name="gdsPath">Absolute path to the .gds file.</param>
    /// <param name="topCellName">Cell to treat as the layout root; pick from
    /// <see cref="ListTopCellsAsync"/> candidates.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed and resolved import result.</returns>
    /// <exception cref="InvalidDataException">Malformed file, undefined cell, or reference cycle.</exception>
    Task<GdsImportResult> ImportAsync(string gdsPath, string topCellName, CancellationToken ct = default);

    /// <summary>
    /// Reads the library structure of the GDS file at <paramref name="gdsPath"/>
    /// and returns the names of all cells that are not referenced by any other
    /// cell — the candidates for the top cell of the layout.
    /// </summary>
    /// <param name="gdsPath">Absolute path to the .gds file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidDataException">Malformed file.</exception>
    Task<IReadOnlyList<string>> ListTopCellsAsync(string gdsPath, CancellationToken ct = default);
}
