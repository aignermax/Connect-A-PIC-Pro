using UnitTests.Import.Gds;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Synthetic large-GDS generator for the import performance probe (issue #811):
/// writes a production-scale library through <see cref="GdsTestWriter"/> — rows
/// of waveguide/device cells chained by top-cell route polygons, plus a block of
/// coincident-pin abutment pairs. Every chained instance gets its OWN cell
/// definition (worst case for flattening, pin detection and draft registration);
/// the abutment block reuses two shared definitions.
/// <para>
/// Geometry convention: all coordinates are database units (1 dbu = 1 nm), Y-up
/// as GDS mandates. Cells are 10×4 µm with <c>in</c>/<c>out</c> labels at the
/// left/right edge midpoints, a 0.5 µm core stripe on the waveguide layer (1,0)
/// and an extent rectangle on (111,0) — the same shape the hierarchy importer
/// tests use, so pin detection and route matching behave identically.
/// </para>
/// </summary>
internal static class GdsImportBenchmark
{
    /// <summary>Database units per micrometer (the standard prologue writes 1 dbu = 1 nm).</summary>
    private const int DbuPerMicron = 1000;

    /// <summary>Cell width in dbu (10 µm).</summary>
    private const int CellWidth = 10 * DbuPerMicron;

    /// <summary>Cell height in dbu (4 µm).</summary>
    private const int CellHeight = 4 * DbuPerMicron;

    /// <summary>Horizontal pitch of chained instances in dbu (20 µm → a 10 µm route gap between cells).</summary>
    private const int ChainPitchX = 20 * DbuPerMicron;

    /// <summary>Row pitch in dbu (20 µm — rows never interact).</summary>
    private const int RowPitchY = 20 * DbuPerMicron;

    /// <summary>
    /// Writes the benchmark library: <paramref name="chainedInstances"/> instances
    /// (<paramref name="deviceCount"/> of them device cells, the rest waveguides)
    /// in rows of <paramref name="instancesPerRow"/>, every consecutive in-row pair
    /// bridged by a top-cell route polygon on layer (1,0); every
    /// <paramref name="chainSplitModulo"/>-th stripe is written as TWO overlapping
    /// polygons (a chain) so multi-polygon networks are exercised.
    /// <paramref name="abutmentPairs"/> extra instance pairs sit in their own rows,
    /// exactly abutting (coincident opposing pins, no route polygon).
    /// </summary>
    public static byte[] CreateLibrary(
        int chainedInstances = 2500,
        int deviceCount = 500,
        int instancesPerRow = 50,
        int chainSplitModulo = 4,
        int abutmentPairs = 100)
    {
        var writer = GdsTestWriter.Create().StandardPrologue("benchmark");

        writer.BeginCell("TOP");
        var plan = PlanChainedInstances(chainedInstances, deviceCount, instancesPerRow);
        foreach (var (cellName, x, y) in plan)
            writer.SRef(cellName, x, y);

        // Route stripes: from the out pin (right edge midpoint) of instance k to
        // the in pin (left edge midpoint) of instance k+1 within each row. The pin
        // sits exactly ON the stripe's end edge → route-derivation touch distance 0.
        var stripeCount = 0;
        for (var i = 0; i < plan.Count; i++)
        {
            if ((i + 1) % instancesPerRow == 0 || i + 1 >= plan.Count)
                continue; // row end — the out pin stays free.
            var (_, x, y) = plan[i];
            int x1 = x + CellWidth, x2 = x + ChainPitchX;
            int y1 = y + 1750, y2 = y + 2250;
            stripeCount++;
            if (stripeCount % chainSplitModulo == 0)
            {
                int mid = (x1 + x2) / 2;
                // 1 dbu overlap: the two halves touch well within the 0.05 µm chain tolerance.
                writer.Boundary(1, 0, (x1, y1), (mid + 1, y1), (mid + 1, y2), (x1, y2), (x1, y1));
                writer.Boundary(1, 0, (mid, y1), (x2, y1), (x2, y2), (mid, y2), (mid, y1));
            }
            else
            {
                writer.Boundary(1, 0, (x1, y1), (x2, y1), (x2, y2), (x1, y2), (x1, y1));
            }
        }

        // Abutment block: pairs of instances whose cells touch exactly (pitch =
        // cell width) — out pin of the left coincides with in pin of the right.
        var abutmentRowY = -RowPitchY;
        for (var pair = 0; pair < abutmentPairs; pair++)
        {
            int x = (pair % instancesPerRow) * (2 * CellWidth + ChainPitchX);
            int y = abutmentRowY - (pair / instancesPerRow) * RowPitchY;
            writer.SRef("abut_wg", x, y);
            writer.SRef("abut_wg", x + CellWidth, y);
        }
        writer.EndCell();

        // Cell definitions AFTER the top cell (order is irrelevant to the reader).
        foreach (var (cellName, _, _) in plan.DistinctBy(p => p.CellName))
            WaveguideCell(writer, cellName);
        WaveguideCell(writer, "abut_wg");

        return writer.EndLibrary().ToArray();
    }

    /// <summary>
    /// 10×4 µm two-port cell, built like the hierarchy importer test fixtures:
    /// a 0.5 µm core stripe on the waveguide layer (1,0), an extent rectangle on
    /// the non-waveguide layer (111,0), and in/out port labels on (1,10).
    /// </summary>
    private static void WaveguideCell(GdsTestWriter writer, string name) =>
        writer
            .BeginCell(name)
                .Boundary(1, 0, (0, 1750), (CellWidth, 1750), (CellWidth, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (CellWidth, 0), (CellWidth, CellHeight), (0, CellHeight), (0, 0))
                .Text(1, 10, "in", 0, 2000)
                .Text(1, 10, "out", CellWidth, 2000)
            .EndCell();

    /// <summary>
    /// The chained instance plan: (cell name, X, Y) in dbu, row-major. Devices are
    /// spread evenly across the chain (every chainedInstances/deviceCount-th
    /// instance is a device cell); each instance references its own cell definition.
    /// </summary>
    private static List<(string CellName, int X, int Y)> PlanChainedInstances(
        int chainedInstances, int deviceCount, int instancesPerRow)
    {
        var plan = new List<(string, int, int)>(chainedInstances);
        int deviceStride = Math.Max(1, chainedInstances / Math.Max(1, deviceCount));
        for (var i = 0; i < chainedInstances; i++)
        {
            string cellName = i % deviceStride == 0 && i / deviceStride < deviceCount
                ? $"dev_{i / deviceStride:D4}"
                : $"wg_{i:D4}";
            plan.Add((cellName, (i % instancesPerRow) * ChainPitchX, (i / instancesPerRow) * RowPitchY));
        }
        return plan;
    }
}
