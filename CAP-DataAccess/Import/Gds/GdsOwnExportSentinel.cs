namespace CAP_DataAccess.Import.Gds;

/// <summary>
/// Recognizes .gds files produced by our OWN exporters. All Lunima export
/// paths write a top/auxiliary cell whose name starts with
/// <see cref="CellNamePrefix"/> (<c>ConnectAPIC_Design</c> from the nazca and
/// gdsfactory exporters, <c>ConnectAPIC_NazcaPartial</c> from the
/// mixed-backend orchestrator), and no foundry PDK uses that prefix. The
/// sentinel gates the Lunima-specific layer defaults
/// (<see cref="GdsHierarchyImportOptions.LunimaMetalRouteLayers"/>,
/// <see cref="GdsPinDetectionOptions.LunimaElectricalLayers"/>): applying our
/// exporter's metal layer numbers to a foreign foundry file misclassifies its
/// routing.
/// </summary>
public static class GdsOwnExportSentinel
{
    /// <summary>Cell-name prefix every Lunima exporter stamps into its output.</summary>
    public const string CellNamePrefix = "ConnectAPIC_";

    /// <summary>True when any cell in <paramref name="library"/> carries the Lunima export prefix.</summary>
    public static bool IsOwnExport(GdsLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return library.Cells.Keys.Any(
            name => name.StartsWith(CellNamePrefix, StringComparison.Ordinal));
    }
}
