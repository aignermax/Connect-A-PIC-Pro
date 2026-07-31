using System.Collections.ObjectModel;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using UnitTests.Import.Gds;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// End-to-end tests for <see cref="GdsImportService"/> with real parsing
/// (temp .gds files via <see cref="GdsTestWriter"/>), a temp-root
/// <see cref="UserPdkStore"/> and the real <see cref="CustomComponentLibraryRegistrar"/>.
/// </summary>
public class GdsImportServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gdsimport-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefsPath =
        Path.Combine(Path.GetTempPath(), $"lunima-gdsimport-prefs-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (File.Exists(_prefsPath)) File.Delete(_prefsPath);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

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

    private string WriteGds(byte[] content, string fileName = "circuit.gds")
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    private UserPdkStore Store() => new(
        Path.Combine(_root, "user-pdks"), new PdkJsonSaver(), new PdkLoader());

    /// <summary>Wires the real registrar with throwaway library state (pattern from RegistrarPdkNameCasingTests).</summary>
    private sealed class LibrarySink
    {
        public readonly ObservableCollection<ComponentTemplate> Templates = new();
        public readonly ObservableCollection<string> Categories = new();
        public readonly PdkManagerViewModel PdkManager = new();
        public readonly List<PdkDraft> LoadedDrafts = new();
        public readonly UserPreferencesService Preferences;
        public readonly Action<PdkComponentDraft, string, string> Register;

        public LibrarySink(string prefsPath)
        {
            Preferences = new UserPreferencesService(prefsPath);
            var loader = new PdkLoader();
            Register = (draft, pdkName, filePath) =>
                CustomComponentLibraryRegistrar.Register(
                    draft, pdkName, filePath, Templates, Categories, PdkManager,
                    Preferences, loader, LoadedDrafts, () => { }, () => { });
        }
    }

    private sealed class ListProgress : IProgress<string>
    {
        public readonly List<string> Messages = new();
        public void Report(string value) => Messages.Add(value);
    }

    // ── Analyze ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnalyzeAsync_ReportsCandidatesAndLibrarySummary()
    {
        var path = WriteGds(TwoWaveguideLibrary());

        var analysis = await GdsImportService.AnalyzeAsync(path);

        analysis.CellCount.ShouldBe(3);
        analysis.TopCellCandidates.ShouldBe(new[] { "TOP" });
        var top = analysis.TopCells.ShouldHaveSingleItem();
        top.CellName.ShouldBe("TOP");
        top.DirectInstanceCount.ShouldBe(2);
    }

    [Fact]
    public async Task AnalyzeAsync_MissingFile_ThrowsFileNotFound()
    {
        var missing = Path.Combine(_root, "nope.gds");
        await Should.ThrowAsync<FileNotFoundException>(
            () => GdsImportService.AnalyzeAsync(missing));
    }

    // ── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_RegistersDraftsCopiesGdsAndReturnsOutcome()
    {
        var path = WriteGds(TwoWaveguideLibrary());
        var sink = new LibrarySink(_prefsPath);
        var progress = new ListProgress();
        var service = new GdsImportService(Store(), () => Array.Empty<ComponentTemplate>(), sink.Register);

        var outcome = await service.ImportAsync(path, "TOP", null, progress);

        // Outcome data.
        outcome.TopCellName.ShouldBe("TOP");
        outcome.Mode.ShouldBe(GdsHierarchyImportMode.ExplodeHierarchy);
        outcome.RegisteredComponents.ShouldBe(new[]
        {
            new GdsRegisteredComponent("wgA", "wgA"),
            new GdsRegisteredComponent("wgB", "wgB"),
        });
        outcome.Instances.Count.ShouldBe(2);
        outcome.Connections.Count.ShouldBe(1);
        outcome.GdsFileName.ShouldBe("circuit.gds");
        outcome.UserPdkName.ShouldBe("GDS Import - circuit");
        progress.Messages.ShouldNotBeEmpty();

        // The .gds was copied next to the user-PDK JSON.
        var storeRoot = Store().RootDirectory;
        File.Exists(Path.Combine(storeRoot, "circuit.gds")).ShouldBeTrue();

        // The user PDK round-trips through the loader (validation included).
        outcome.UserPdkPath.ShouldNotBeNull();
        var pdk = new PdkLoader().LoadFromFileForEditing(outcome.UserPdkPath);
        pdk.Name.ShouldBe("GDS Import - circuit");
        pdk.ProcessAgnostic.ShouldBeTrue();
        pdk.Process.ShouldBeNull();
        pdk.Components.Count.ShouldBe(2);
        var wgA = pdk.Components.Single(c => c.Name == "wgA");
        wgA.Pins.Count.ShouldBe(2);
        wgA.SMatrix.ShouldBeNull();
        wgA.RawCodeBackend.ShouldBe("nazca");
        wgA.RawCode.ShouldContain("cellname=\"wgA\"");
        wgA.RawCode.ShouldNotContain(GdsHierarchyImporterToken());
        wgA.OutlinePolygons.ShouldNotBeNull();

        // Runtime registration happened through the registrar seam.
        sink.Templates.Select(t => t.Name).ShouldBe(new[] { "wgA", "wgB" }, ignoreOrder: true);
        sink.Templates.ShouldAllBe(t => t.PdkSource == "GDS Import - circuit" && t.IsCustom);
        sink.PdkManager.LoadedPdks.ShouldContain(p => p.Name == "GDS Import - circuit");
        sink.Preferences.GetUserPdkPaths().ShouldContain(outcome.UserPdkPath);
    }

    [Fact]
    public async Task ImportAsync_KnownTemplateCell_ResolvesInsteadOfRegistering()
    {
        var path = WriteGds(TwoWaveguideLibrary());
        var sink = new LibrarySink(_prefsPath);
        var wgATemplate = new ComponentTemplate
        {
            Name = "wgA",
            PdkSource = "testpdk",
            WidthMicrometers = 10,
            HeightMicrometers = 4,
            PinDefinitions = new[]
            {
                new PinDefinition("in", 0, 2, 180),
                new PinDefinition("out", 10, 2, 0),
            },
        };
        var service = new GdsImportService(
            Store(), () => new[] { wgATemplate }, sink.Register);

        var outcome = await service.ImportAsync(path, "TOP", null, null);

        outcome.Instances[0].KnownComponentIdentifier.ShouldBe("wgA");
        outcome.Instances[0].PdkSource.ShouldBe("testpdk");
        outcome.RegisteredComponents.ShouldBe(new[] { new GdsRegisteredComponent("wgB", "wgB") });
        sink.Templates.Select(t => t.Name).ShouldBe(new[] { "wgB" });
    }

    // ── .gds copy collision handling ─────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_SameNameDifferentContent_GdsCopyGetsSuffixed()
    {
        var path = WriteGds(TwoWaveguideLibrary());
        var storeRoot = Store().RootDirectory;
        Directory.CreateDirectory(storeRoot);
        var colliding = Path.Combine(storeRoot, "circuit.gds");
        File.WriteAllBytes(colliding, new byte[] { 1, 2, 3, 4 });

        var outcome = await new GdsImportService(Store()).ImportAsync(path, "TOP", null, null);

        outcome.GdsFileName.ShouldBe("circuit-2.gds");
        outcome.UserPdkName.ShouldBe("GDS Import - circuit-2");
        File.ReadAllBytes(colliding).ShouldBe(new byte[] { 1, 2, 3, 4 },
            "a different file with the same name must never be overwritten");
        File.Exists(Path.Combine(storeRoot, "circuit-2.gds")).ShouldBeTrue();
    }

    [Fact]
    public async Task ImportAsync_SameNameIdenticalContent_ReusesExistingCopy()
    {
        var path = WriteGds(TwoWaveguideLibrary());
        var storeRoot = Store().RootDirectory;
        Directory.CreateDirectory(storeRoot);
        File.Copy(path, Path.Combine(storeRoot, "circuit.gds"));

        var outcome = await new GdsImportService(Store()).ImportAsync(path, "TOP", null, null);

        outcome.GdsFileName.ShouldBe("circuit.gds");
        Directory.GetFiles(storeRoot, "*.gds").ShouldHaveSingleItem()
            .ShouldBe(Path.Combine(storeRoot, "circuit.gds"));
    }

    // ── Errors / edge cases ──────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_MissingFile_ThrowsFileNotFound()
    {
        var missing = Path.Combine(_root, "nope.gds");
        var ex = await Should.ThrowAsync<FileNotFoundException>(
            () => new GdsImportService(Store()).ImportAsync(missing, "TOP", null, null));
        ex.Message.ShouldContain("nope.gds");
    }

    [Fact]
    public async Task ImportAsync_UnknownTopCell_ThrowsWithCandidates()
    {
        var path = WriteGds(TwoWaveguideLibrary());

        var ex = await Should.ThrowAsync<InvalidDataException>(
            () => new GdsImportService(Store()).ImportAsync(path, "MISSING", null, null));

        ex.Message.ShouldContain("MISSING");
        ex.Message.ShouldContain("TOP"); // lists the top-cell candidates
    }

    [Fact]
    public async Task ImportAsync_UnpersistableDraft_RegistersNothingAndWarns()
    {
        // "blob" has no waveguide-layer geometry and no port labels → no pins →
        // the PDK loader would reject it, so the service skips it with a warning.
        var library = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("blob", 0, 0)
            .EndCell()
            .BeginCell("blob")
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
            .EndCell()
            .EndLibrary()
            .ToArray();
        var path = WriteGds(library);
        var sink = new LibrarySink(_prefsPath);
        var service = new GdsImportService(Store(), () => Array.Empty<ComponentTemplate>(), sink.Register);

        var outcome = await service.ImportAsync(path, "TOP", null, null);

        outcome.RegisteredComponents.ShouldBeEmpty();
        outcome.UserPdkPath.ShouldBeNull();
        outcome.GdsFileName.ShouldBeNull("no draft needs the .gds copy when nothing is registered");
        outcome.Warnings.ShouldContain(w => w.Contains("blob") && w.Contains("not registered"));
        sink.Templates.ShouldBeEmpty();
        Directory.Exists(Store().RootDirectory).ShouldBeFalse(
            "no PDK and no .gds copy — the store root is never created");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string GdsHierarchyImporterToken() => "{GdsFileName}";
}

/// <summary>GDS fixture cell builders for the service tests.</summary>
file static class GdsImportServiceTestCells
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
