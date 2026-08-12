using CAP_DataAccess.Import.Gds;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.Services.GdsImport;

/// <summary>Scratch probe on the committed mixed-backend fixture (never modifies it).</summary>
public class ScratchMixedBackendProbe
{
    private readonly ITestOutputHelper _out;
    public ScratchMixedBackendProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public async Task Probe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Tools", "gds-test-data")))
            dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, "Tools", "gds-test-data", "test.gds");

        var library = await new GdsReader().ReadAsync(File.OpenRead(path));
        var result = await GdsHierarchyImporter.ImportAsync(
            library, "ConnectAPIC_Design", new GdsHierarchyImportOptions());

        _out.WriteLine($"instances={result.Instances.Count} drafts={result.ImportedCellDrafts.Count} " +
                       $"connections={result.Connections.Count} frozenWg={result.TopCellWaveguidePolygons.Count}");
        foreach (var i in result.Instances)
            _out.WriteLine($"  inst {i.InstanceName} cell={i.CellName} rot={i.RotationDegrees:0.##}");
        foreach (var c in result.Connections)
        {
            string End(GdsPinEndpoint e) => e.InstanceIndex < 0
                ? $"PORT.{e.PinName}"
                : $"{result.Instances[e.InstanceIndex].InstanceName}.{e.PinName}";
            _out.WriteLine($"  conn {End(c.A)} <-> {End(c.B)}");
        }
        foreach (var w in result.Warnings.Take(12)) _out.WriteLine($"  W {w}");
        foreach (var i in result.Infos.Take(12)) _out.WriteLine($"  I {i}");
    }
}
