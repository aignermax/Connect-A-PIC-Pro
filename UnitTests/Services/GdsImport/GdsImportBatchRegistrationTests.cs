using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Library;
using Shouldly;
using UnitTests.Import.Gds;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Covers the registration seam of <see cref="GdsImportService"/> after issue
/// #830: the design scope registers each imported set with EXACTLY ONE
/// callback invocation carrying ALL drafts — the successor of the old
/// per-draft batch scope, keeping the component library from re-filtering
/// and rewriting state once per imported cell.
/// </summary>
public class GdsImportBatchRegistrationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gdsbatch-" + Guid.NewGuid().ToString("N"));
    private readonly GdsDesignScopeTestHost _host = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        _host.Dispose();
    }

    /// <summary>TOP with two abutting 10×4 µm waveguide cells (wgA → wgB), gdsfactory-style.</summary>
    private static byte[] TwoWaveguideLibrary() => GdsTestWriter.Create()
        .StandardPrologue()
        .BeginCell("TOP")
            .SRef("wgA", 0, 0)
            .SRef("wgB", 10000, 0)
        .EndCell()
        .WaveguideCell("wgA")
        .WaveguideCell("wgB")
        .EndLibrary()
        .ToArray();

    private string WriteGds(byte[] content)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "circuit.gds");
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public async Task ImportAsync_RegistersAllDraftsWithASingleCallbackInvocation()
    {
        var path = WriteGds(TwoWaveguideLibrary());
        var service = _host.CreateService(() => Array.Empty<ComponentTemplate>());

        await service.ImportAsync(path, "TOP", null, null);

        var registered = _host.LoadedDrafts.ShouldHaveSingleItem(
            "one register-callback invocation per import, not one per draft");
        registered.Components.Count.ShouldBe(2, "the single invocation carries every draft");
        _host.Templates.Select(t => t.Name).ShouldBe(new[] { "wgA", "wgB" }, ignoreOrder: true);
    }
}

/// <summary>GDS fixture cell builders (same shape as GdsImportServiceTests' waveguide cell).</summary>
file static class GdsBatchRegistrationTestCells
{
    /// <summary>
    /// 10×4 µm gdsfactory-style waveguide: a 0.5 µm core stripe on the waveguide
    /// layer (1,0), an extent rectangle on (111,0), and in/out port labels on (1,10).
    /// </summary>
    public static GdsTestWriter WaveguideCell(this GdsTestWriter writer, string name) =>
        writer
            .BeginCell(name)
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(1, 10, "in", 0, 2000)
                .Text(1, 10, "out", 10000, 2000)
            .EndCell();
}
