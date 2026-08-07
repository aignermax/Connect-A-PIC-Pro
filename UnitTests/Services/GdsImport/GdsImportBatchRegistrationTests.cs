using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;
using UnitTests.Import.Gds;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Covers the registration batching seam of <see cref="GdsImportService"/>: the
/// service must open exactly one batch scope around the whole per-draft
/// registration loop — the scope is what keeps the component library from
/// re-filtering and rewriting preferences once per imported cell.
/// </summary>
public class GdsImportBatchRegistrationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gdsbatch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
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

    private UserPdkStore Store() => new(
        Path.Combine(_root, "user-pdks"), new PdkJsonSaver(), new PdkLoader());

    [Fact]
    public async Task ImportAsync_RegistersEveryDraftInsideASingleBatchScope()
    {
        var path = WriteGds(TwoWaveguideLibrary());
        int scopeOpens = 0, scopeDepth = 0, registrations = 0, registrationsInsideScope = 0;
        var service = new GdsImportService(
            Store(),
            () => Array.Empty<ComponentTemplate>(),
            (_, _, _) =>
            {
                registrations++;
                if (scopeDepth > 0) registrationsInsideScope++;
            },
            () =>
            {
                scopeOpens++;
                scopeDepth++;
                return new ScopeProbe(() => scopeDepth--);
            });

        await service.ImportAsync(path, "TOP", null, null);

        registrations.ShouldBe(2);
        scopeOpens.ShouldBe(1, "one batch scope per import, not per draft");
        registrationsInsideScope.ShouldBe(2, "every registration must happen inside the scope");
        scopeDepth.ShouldBe(0, "the scope must be closed by the time the import returns");
    }

    [Fact]
    public async Task ImportAsync_WithoutBatchFactory_StillRegistersEveryDraft()
    {
        var path = WriteGds(TwoWaveguideLibrary());
        var registrations = 0;
        var service = new GdsImportService(
            Store(),
            () => Array.Empty<ComponentTemplate>(),
            (_, _, _) => registrations++);

        await service.ImportAsync(path, "TOP", null, null);

        registrations.ShouldBe(2, "a null batch factory means unbatched registration, never none");
    }

    private sealed class ScopeProbe : IDisposable
    {
        private readonly Action _onDispose;
        public ScopeProbe(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => _onDispose();
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
