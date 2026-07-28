using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Connections;
using CAP_Core.Export;

namespace CAP.Avalonia.Services;

/// <summary>
/// Builds the post-export "N connections omitted" report shared by the Nazca and
/// gdsfactory export flows. Both exporters silently drop the same connections via
/// <see cref="ExportableConnections.IsExportable"/> (a missing, blocked, or invalid
/// route must never appear as GDS geometry) — this mirrors their analysis-tool
/// exclusion so the report names exactly the connections left out of the GDS.
/// </summary>
public static class SkippedConnectionReporter
{
    /// <summary>
    /// The live connections that were left out of the exported geometry: their route is
    /// missing, blocked, or invalid. Connections touching a virtual analysis-tool pin are
    /// excluded — those never carry export geometry regardless of routing state.
    /// </summary>
    public static IReadOnlyList<WaveguideConnection> CollectSkipped(DesignCanvasViewModel canvas) =>
        canvas.Connections
            .Select(vm => vm.Connection)
            .Where(c => c.StartPin?.ParentComponent?.IsAnalysisTool != true)
            .Where(c => c.EndPin?.ParentComponent?.IsAnalysisTool != true)
            .CollectSkipped();

    /// <summary>Formats a connection as "StartComponent.StartPin → EndComponent.EndPin".</summary>
    public static string Describe(WaveguideConnection connection) =>
        $"{connection.StartPin.ParentComponent.Identifier}.{connection.StartPin.Name} → " +
        $"{connection.EndPin.ParentComponent.Identifier}.{connection.EndPin.Name}";
}
