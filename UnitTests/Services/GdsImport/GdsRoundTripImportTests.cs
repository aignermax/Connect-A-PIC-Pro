using System.Collections.ObjectModel;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Import.Gds;
using CAP_Core.Components.Core;
using Shouldly;
using UnitTests.Export;
using UnitTests.Import.Gds;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Round-trip regression tests for the bug "components are missing after
/// exporting a design to GDS and re-importing it". Two independent root causes
/// are pinned here:
/// <list type="number">
/// <item>The export's footer nested the design under nazca's default 'nazca'
/// wrapper cell (<c>design.put()</c>), so the written GDS offered only that
/// wrapper as the top-cell candidate — an explode import of it yielded ONE
/// black-box draft of the whole design (or nothing at all). The export now
/// writes the design as the GDS top cell (<c>topcells=[design]</c>), and the
/// analyzer looks through such pure pass-through wrappers for files exported
/// before the fix.</item>
/// <item>Demofab (bundled Demo PDK) cells label their pins as TEXT on (501, 1)
/// — demofab's <c>bb_pin_text</c> layer — which pin detection did not read, so
/// e.g. an MMI came back with zero pins and was skipped as unpersistable. The
/// layer is now a default port layer.</item>
/// </list>
/// The full-circle test runs the generated script with real nazca and is
/// skipped cleanly without it (mirrors <see cref="GdsExportFullCircleTests"/>).
/// </summary>
[Trait("Category", "Slow")]
public class GdsRoundTripImportTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gds-roundtrip-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefsPath =
        Path.Combine(Path.GetTempPath(), $"lunima-gds-roundtrip-prefs-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (File.Exists(_prefsPath)) File.Delete(_prefsPath);
    }

    // ── Wrapper unwrapping (files exported before the footer fix) ───────────

    [Fact]
    public async Task AnalyzeAsync_PassThroughWrapperTopCell_OffersWrappedDesignCell()
    {
        // A pre-fix own export: the design cell hides under nazca's default
        // wrapper, whose only element is one untransformed SREF to the design.
        var gdsPath = WriteGds(
            ("nazca", w => w.SRef("ConnectAPIC_Design", 0, 0)),
            ("ConnectAPIC_Design", w => w.SRef("wgA", 0, 0).SRef("wgB", 10000, 0)),
            ("wgA", WaveguideCell),
            ("wgB", WaveguideCell));

        var analysis = await GdsImportService.AnalyzeAsync(gdsPath);

        analysis.TopCellCandidates.ShouldBe(new[] { "ConnectAPIC_Design" });
        analysis.TopCells.ShouldBe(new[] { new GdsTopCellSummary("ConnectAPIC_Design", 2) });
    }

    [Fact]
    public async Task AnalyzeAsync_WrapperWithOwnGeometry_KeepsWrapperCandidate()
    {
        // A top cell that adds routing geometry of its own next to the reference
        // is NOT a pass-through wrapper — unwrapping it would silently drop that
        // geometry from the import, so it stays the candidate.
        var gdsPath = WriteGds(
            ("TOP", w => w
                .Boundary(1, 0, (0, 0), (1000, 0), (1000, 500), (0, 500), (0, 0))
                .SRef("SUB", 0, 0)),
            ("SUB", WaveguideCell));

        var analysis = await GdsImportService.AnalyzeAsync(gdsPath);

        analysis.TopCellCandidates.ShouldBe(new[] { "TOP" });
    }

    [Fact]
    public async Task AnalyzeAsync_TransformedWrapperReference_KeepsWrapperCandidate()
    {
        // A single reference WITH a transform is not a pass-through either: the
        // wrapper applies rotation/magnification the unwrapped cell would lose.
        var gdsPath = WriteGds(
            ("TOP", w => w.SRef("SUB", 0, 0, angleDegrees: 90.0)),
            ("SUB", WaveguideCell));

        var analysis = await GdsImportService.AnalyzeAsync(gdsPath);

        analysis.TopCellCandidates.ShouldBe(new[] { "TOP" });
    }

    [Fact]
    public async Task ImportAsync_PreFixExportFile_ExplodesDesignCellComponents()
    {
        // End-to-end over a file shaped like a pre-fix own export: the analyzer
        // surfaces the design cell, and importing it explodes to the placed
        // components (not one black box of the whole design).
        var gdsPath = WriteGds(
            ("nazca", w => w.SRef("ConnectAPIC_Design", 0, 0)),
            ("ConnectAPIC_Design", w => w.SRef("wgA", 0, 0).SRef("wgB", 10000, 0)),
            ("wgA", WaveguideCell),
            ("wgB", WaveguideCell));

        var analysis = await GdsImportService.AnalyzeAsync(gdsPath);
        var sink = new LibrarySink(_prefsPath);
        var service = new GdsImportService(Store(), () => Array.Empty<ComponentTemplate>(), sink.Register);
        var outcome = await service.ImportAsync(gdsPath, analysis.TopCellCandidates[0], null, null);

        outcome.Warnings.ShouldBeEmpty();
        outcome.RegisteredComponents.Count.ShouldBe(2);
        outcome.Instances.Count.ShouldBe(2);
        outcome.Connections.Count.ShouldBe(1);
    }

    // ── Demofab pin labels on (501, 1) ───────────────────────────────────────

    [Fact]
    public void PinDetector_DemofabBbPinTextLayer_YieldsLabelPin()
    {
        // nazca demofab marks black-box cell pins on layer 501: marker polygons on
        // (501, 0), the pin NAME as text on (501, 1) (demofab's layer table:
        // bb_pin / bb_pin_text). Our own exports via the bundled Demo PDK carry
        // these labels, so the default port layers must recognize them.
        var cell = new FlattenedGdsCell { CellName = "mmi1x2_sh" };
        cell.Texts.Add(new GdsText
        {
            Layer = 501,
            TextType = 1,
            Text = "a0",
            Position = new GdsPoint(0, 3),
        });

        var pins = GdsPinDetector.Detect(
            cell, new GdsBoundingBox(0, -27.5, 80, 27.5));

        pins.Count.ShouldBe(1);
        pins[0].Name.ShouldBe("a0");
        pins[0].Source.ShouldBe(DetectedPinSource.Label);
    }

    [Fact]
    public async Task ImportAsync_CallerLayerListWithoutDemofabLayer_StillDetectsDemofabPinLabels()
    {
        // The import dialog always passes an EXPLICIT port-layer list (its fields
        // default to the gdsfactory "1,10") — that must not drop the demofab
        // pin-label layer of our own exports, or the MMI comes back pinless and
        // is skipped as unpersistable (the reported "components are missing").
        var gdsPath = WriteGds(
            ("TOP", w => w.SRef("mmi1x2_sh", 0, 0)),
            ("mmi1x2_sh", w => w
                .Boundary(20, 0, (0, -27500), (80000, -27500), (80000, 27500), (0, 27500), (0, -27500))
                .Text(501, 1, "a0", 0, 3000)
                .Text(501, 1, "b0", 79700, 2000)
                .Text(501, 1, "b1", 79700, -2000)));

        var sink = new LibrarySink(_prefsPath);
        var service = new GdsImportService(Store(), () => Array.Empty<ComponentTemplate>(), sink.Register);
        var dialogDefaultOptions = new GdsHierarchyImportOptions
        {
            PinDetection = new GdsPinDetectionOptions { PortLayers = [(1, 10)] },
        };
        var outcome = await service.ImportAsync(gdsPath, "TOP", dialogDefaultOptions, null);

        outcome.Warnings.ShouldNotContain(w => w.Contains("was not registered", StringComparison.Ordinal));
        outcome.RegisteredComponents.ShouldContain(r => r.CellDraftName == "mmi1x2_sh");
        sink.Templates.ShouldContain(t => t.Name == "mmi1x2_sh");
        var mmiTemplate = sink.Templates.First(t => t.Name == "mmi1x2_sh");
        mmiTemplate.PinDefinitions.Select(p => p.Name).ShouldBe(new[] { "a0", "b0", "b1" });
    }

    // ── Full circle with the real nazca engine ───────────────────────────────

    [SkippableFact]
    public async Task RoundTrip_OwnNazcaExport_DesignIsTopCell_AllComponentsReimport()
    {
        var python = await FindNazcaPythonAsync();
        Skip.If(python == null, "No Python with nazca available — full-circle proof needs the real engine.");

        // 1. The user's design: input grating coupler → MMI → output grating
        // coupler (rotated 180°), wired with waveguide connections (Demo PDK).
        var canvas = new DesignCanvasViewModel();
        var gcIn = DemoPdkComponent("GC_in", "demo.io", 100, 19, 0, 9.5, 100, 300,
            ("fiber", 0, 9.5, 180), ("waveguide", 100, 9.5, 0));
        var mmi = DemoPdkComponent("MMI", "demo.mmi1x2_sh", 80, 55, 0, 27.5, 400, 282,
            ("in", 0, 27.5, 180), ("out1", 80, 25.5, 0), ("out2", 80, 29.5, 0));
        var gcOut = DemoPdkComponent("GC_out", "demo.io", 100, 19, 0, 9.5, 900, 300,
            ("fiber", 0, 9.5, 180), ("waveguide", 100, 9.5, 0));
        gcOut.RotationDegrees = 180;
        canvas.AddComponent(gcIn, "Grating Coupler");
        canvas.AddComponent(mmi, "1x2 MMI Splitter");
        canvas.AddComponent(gcOut, "Grating Coupler");
        canvas.ConnectPins(Pin(gcIn, "waveguide"), Pin(mmi, "in"));
        canvas.ConnectPins(Pin(mmi, "out2"), Pin(gcOut, "waveguide"));
        await canvas.RecalculateRoutesAsync();

        // 2. Export: the design must be the GDS top cell, not the 'nazca' wrapper.
        var script = new SimpleNazcaExporter().Export(canvas);
        script.ShouldContain("nd.export_gds(topcells=[design], filename=gds_filename)");
        script.ShouldNotContain("design.put()");

        var exportDir = Path.Combine(_root, "export");
        Directory.CreateDirectory(exportDir);
        var scriptPath = Path.Combine(exportDir, "chip.py");
        await File.WriteAllTextAsync(scriptPath, script);
        var run = await SiepicRealGeometryExportTests.RunPythonAsync(python, exportDir, scriptPath);
        run.ExitCode.ShouldBe(0, $"nazca export script failed:\n{run.StdOut}\n{run.StdErr}");
        var gdsPath = Path.ChangeExtension(scriptPath, ".gds");
        File.Exists(gdsPath).ShouldBeTrue($"script did not write {gdsPath}:\n{run.StdOut}");

        // 3. The exported GDS: ConnectAPIC_Design is the ONLY top-cell candidate.
        GdsLibrary library;
        await using (var stream = File.OpenRead(gdsPath))
            library = await new GdsReader().ReadAsync(stream);
        library.TopCellCandidates.ShouldBe(new[] { "ConnectAPIC_Design" },
            $"cells: {string.Join(", ", library.Cells.Keys)}");

        // 4. Analyze (the dialog's candidate list) offers the design cell.
        var analysis = await GdsImportService.AnalyzeAsync(gdsPath);
        analysis.TopCellCandidates.ShouldBe(new[] { "ConnectAPIC_Design" });

        // 5. Explode import: both demofab components come back as drafts — the
        // MMI with its (501, 1) pin labels a0/b0/b1, not dropped as pinless.
        var sink = new LibrarySink(_prefsPath);
        var service = new GdsImportService(Store(), () => Array.Empty<ComponentTemplate>(), sink.Register);
        var outcome = await service.ImportAsync(gdsPath, "ConnectAPIC_Design", null, null);

        outcome.Warnings.ShouldNotContain(w => w.Contains("was not registered", StringComparison.Ordinal));
        outcome.RegisteredComponents.Count.ShouldBe(2, string.Join("; ", outcome.Warnings));
        outcome.RegisteredComponents.ShouldContain(r => r.CellDraftName == "mmi1x2_sh");
        outcome.RegisteredComponents.ShouldContain(r => r.CellDraftName.StartsWith("io_", StringComparison.Ordinal));
        outcome.Instances.Count.ShouldBe(3);
        // The routed waveguide connections are flattened into top-cell geometry by
        // nazca, so no abutment connections reconstruct — but the top cell's own
        // waveguide-layer polygons come back as frozen, non-re-routable paths.
        // The routed waveguide connections are flattened into top-cell polygon
        // chains by nazca. The gcIn.waveguide → mmi.in chain spans exactly two
        // pins and restores as a real, re-routable connection (route-derived);
        // the mmi.out2 → gcOut.waveguide chain entangles three pins (the
        // rot180 gcOut's two heuristic edge pins + mmi.b1) into a junction
        // network, which v1 deliberately leaves frozen with an info note.
        var connection = outcome.Connections.ShouldHaveSingleItem();
        connection.IsRouteDerived.ShouldBeTrue();
        connection.IsElectrical.ShouldBeFalse();
        connection.A.PinName.ShouldBe("a0");
        connection.B.PinName.ShouldBe("heur_2");
        outcome.Warnings.ShouldBeEmpty("restored/frozen accounting is informational now");
        outcome.Infos.ShouldContain(i => i.Contains("junction with 3 pins"));
        outcome.Infos.ShouldContain(i => i.Contains("restored as 1 real connection(s)"));
        outcome.Infos.ShouldContain(i => i.Contains("imported as frozen paths (not re-routable)"));
        outcome.TopCellWaveguidePolygons.Count.ShouldBe(3,
            "the junction network's polygons ride the group as frozen, non-re-routable paths");

        // The registered MMI template carries the demofab pin names.
        sink.Templates.ShouldContain(t => t.Name == "mmi1x2_sh");
        var mmiTemplate = sink.Templates.First(t => t.Name == "mmi1x2_sh");
        mmiTemplate.PinDefinitions.Select(p => p.Name).ShouldBe(new[] { "a0", "b0", "b1" });

        // 6. Placement: all three components land on the canvas — none missing.
        var canvas2 = new DesignCanvasViewModel();
        var report = await new GdsPlacementExecutor(canvas2, null, () => sink.Templates.ToList())
            .ExecuteAsync(GdsPlacementPlan.FromOutcome(outcome));
        report.SkippedPlacements.ShouldBeEmpty();
        report.PlacedCount.ShouldBe(3);
        report.ConnectedCount.ShouldBe(1);
        // The executor wraps the import in one group — the canvas root holds that group.
        var group = canvas2.Components.ShouldHaveSingleItem().Component.ShouldBeOfType<ComponentGroup>();
        group.GetAllComponentsRecursive().Count().ShouldBe(3);
        group.InternalPaths.ShouldContain(p => p.StartPin == null,
            "the junction network's polygons ride the group as pin-less frozen paths");
        group.InternalPaths.ShouldContain(p => p.StartPin != null,
            "the restored connection freezes into the group with its pins");
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    /// <summary>Writes a cell-tree fixture: (cell name, content builder) pairs in file order.</summary>
    private string WriteGds(params (string Name, Func<GdsTestWriter, GdsTestWriter> Build)[] cells)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, $"fixture-{Guid.NewGuid():N}.gds");
        var writer = GdsTestWriter.Create().StandardPrologue();
        foreach (var (name, build) in cells)
            build(writer.BeginCell(name)).EndCell();
        File.WriteAllBytes(path, writer.EndLibrary().ToArray());
        return path;
    }

    /// <summary>
    /// 10×4 µm gdsfactory-style waveguide: a 0.5 µm core stripe on (1, 0) and
    /// in/out port labels on (1, 10) (same shape as GdsExportFullCircleTests).
    /// </summary>
    private static GdsTestWriter WaveguideCell(GdsTestWriter writer) =>
        writer
            .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
            .Text(1, 10, "in", 0, 2000)
            .Text(1, 10, "out", 10000, 2000);

    private static PhysicalPin Pin(Component component, string name) =>
        component.PhysicalPins.First(p => p.Name == name);

    /// <summary>Builds a component shaped like a Demo PDK template instance.</summary>
    private static Component DemoPdkComponent(
        string identifier, string nazcaFunction,
        double width, double height, double originOffsetX, double originOffsetY,
        double x, double y,
        params (string Name, double Ox, double Oy, double Angle)[] pins)
    {
        var component = TestComponentFactory.CreateBasicComponent();
        component.Identifier = identifier;
        component.NazcaFunctionName = nazcaFunction;
        component.WidthMicrometers = width;
        component.HeightMicrometers = height;
        component.NazcaOriginOffsetX = originOffsetX;
        component.NazcaOriginOffsetY = originOffsetY;
        component.PhysicalX = x;
        component.PhysicalY = y;
        component.PhysicalPins.Clear();
        foreach (var (name, ox, oy, angle) in pins)
        {
            component.PhysicalPins.Add(new PhysicalPin
            {
                Name = name,
                ParentComponent = component,
                OffsetXMicrometers = ox,
                OffsetYMicrometers = oy,
                AngleDegrees = angle,
            });
        }
        return component;
    }

    private UserPdkStore Store() => new(
        Path.Combine(_root, "user-pdks"), new PdkJsonSaver(), new PdkLoader());

    /// <summary>Wires the real registrar with throwaway library state (pattern from GdsImportServiceTests).</summary>
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

    private static async Task<string?> FindNazcaPythonAsync()
    {
        var envs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunima", "envs");
        if (Directory.Exists(envs))
        {
            foreach (var root in Directory.GetDirectories(envs))
            {
                foreach (var rel in new[] { Path.Combine("Scripts", "python.exe"), Path.Combine("bin", "python") })
                {
                    var py = Path.Combine(root, rel);
                    if (File.Exists(py) && await ProbeNazca(py))
                        return py;
                }
            }
        }

        foreach (var candidate in new[] { "python", "python3" })
        {
            if (await ProbeNazca(candidate))
                return candidate;
        }
        return null;
    }

    private static async Task<bool> ProbeNazca(string python)
    {
        try
        {
            var probe = await SiepicRealGeometryExportTests.RunPythonAsync(
                python, Path.GetTempPath(), "-c", "import nazca");
            return probe.ExitCode == 0;
        }
        catch
        {
            return false;   // not on PATH at all
        }
    }
}
