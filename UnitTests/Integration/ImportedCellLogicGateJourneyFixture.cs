using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Core;
using UnitTests.Analysis.LogicAnalysis;
using UnitTests.Import.Gds;
using UnitTests.Services.GdsImport;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Shared fixture for <see cref="ImportedCellLogicGateJourneyTests"/>: imports the
/// same 2-optical-pin waveguide cell as <see cref="GdsImportJourneyFixture"/>, then
/// splices the imported black-box cell into the signal path of a minimal buffer gate
/// group and pre-computes the rung-4 products the journey's steps assert against:
/// the gate group's truth table (real S-matrix extraction), the identical group
/// without the imported cell (a unity-through fixture waveguide — the optical-
/// neutrality baseline), and the <see cref="LogicNetworkEvaluator"/> the
/// <see cref="LogicNetworkAssembler"/> derives from the canvas.
/// </summary>
public sealed class ImportedCellLogicGateJourneyFixture : IAsyncLifetime
{
    /// <summary>Laser wavelength the tables and the network are extracted at (standard red).</summary>
    public const int WavelengthNm = 1550;

    /// <summary>The gate group's normalized logic threshold (power ≥ threshold is logic 1).</summary>
    public const double Threshold = 0.5;

    /// <summary>The gate id the whole journey runs over.</summary>
    public const string GateName = "BUF1";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-imported-cell-gate-" + Guid.NewGuid().ToString("N"));
    private string? _savedPath;

    /// <summary>The design-scope host the import registered the cell's template into.</summary>
    public GdsDesignScopeTestHost Host { get; private set; } = null!;

    /// <summary>The import result (registered drafts, warnings, resolved PDK name).</summary>
    public GdsImportOutcome Outcome { get; private set; } = null!;

    /// <summary>The gate group whose sole child is the imported cell, placed on the canvas.</summary>
    public ComponentGroup SplicedGroup { get; private set; } = null!;

    /// <summary>The same group built over a unity-through fixture waveguide instead of the imported cell.</summary>
    public ComponentGroup BaselineGroup { get; private set; } = null!;

    /// <summary>The canvas carrying <see cref="SplicedGroup"/> as its only top-level component.</summary>
    public DesignCanvasViewModel Canvas { get; private set; } = null!;

    /// <summary>The truth table extracted from <see cref="SplicedGroup"/> before save.</summary>
    public TruthTable SplicedTable { get; private set; } = null!;

    /// <summary>The truth table extracted from <see cref="BaselineGroup"/> — the neutrality baseline.</summary>
    public TruthTable BaselineTable { get; private set; } = null!;

    /// <summary>The logic network assembled from the canvas before save.</summary>
    public LogicNetworkEvaluator Network { get; private set; } = null!;

    /// <summary>Imports the cell and pre-computes every product the journey's steps assert.</summary>
    public async Task InitializeAsync()
    {
        Host = new GdsDesignScopeTestHost();
        var gdsPath = WriteGds();
        var service = Host.CreateService(() => Array.Empty<ComponentTemplate>());
        Outcome = await service.ImportAsync(gdsPath, "TOP", null, null);
        var importedTemplate = Host.Templates.Single(t =>
            t.Name == "wg" && t.PdkSource == Outcome.UserPdkName);

        SplicedGroup = BuildBufferGroup(importedTemplate, ("in", "in"), ("out", "out"));
        BaselineGroup = BuildBufferGroup(
            LogicGateFixtureFactory.CreateWaveguideTemplate(), ("in", "a0"), ("out", "b0"));

        Canvas = new DesignCanvasViewModel();
        Canvas.InitializeAStarRouting(-500, -500, 500, 500);
        Canvas.AddComponent(SplicedGroup);

        var extractor = new TruthTableExtractor();
        SplicedTable = await Extract(extractor, SplicedGroup);
        BaselineTable = await Extract(extractor, BaselineGroup);
        Network = await Assemble(Canvas);
    }

    /// <summary>No shared state beyond the host and the temp folder to release.</summary>
    public Task DisposeAsync()
    {
        Host.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    /// <summary>The roles the gate group persists: input "in", output "out".</summary>
    public static TruthTablePinAssignment Roles() => new()
    {
        InputPinNames = new List<string> { "in" },
        OutputPinNames = new List<string> { "out" },
        Threshold = Threshold,
    };

    /// <summary>
    /// Assembles the canvas's logic network through <see cref="LogicNetworkAssembler"/>
    /// — the rung-4 re-assembly path loaded designs go through (issue #988).
    /// </summary>
    public static async Task<LogicNetworkEvaluator> Assemble(DesignCanvasViewModel canvas)
    {
        var components = canvas.Components.Select(c => c.Component).ToList();
        var connections = canvas.Connections.Select(c => c.Connection).ToList();
        return await new LogicNetworkAssembler().AssembleAsync(components, connections, WavelengthNm);
    }

    /// <summary>Saves the canvas to .lun through the real save path (idempotent — reuses the first save).</summary>
    public async Task<string> SaveDesign()
    {
        if (_savedPath is not null)
            return _savedPath;

        _savedPath = Path.Combine(_root, "journey.lun");
        await GdsImportJourneyFixture.SaveToFile(
            GdsImportJourneyFixture.CreateFileOperations(Canvas, Host), _savedPath);
        return _savedPath;
    }

    /// <summary>
    /// Builds a one-child buffer gate group over the given template, exposing the
    /// child's pins under the mapped external names, and persists the roles.
    /// </summary>
    private static ComponentGroup BuildBufferGroup(
        ComponentTemplate template, params (string External, string Internal)[] pinMap)
    {
        var child = ComponentTemplates.CreateFromTemplate(template, 0, 0);
        var group = new ComponentGroup(GateName);
        group.AddChild(child);
        foreach (var (external, internalName) in pinMap)
        {
            group.AddExternalPin(new GroupPin
            {
                Name = external,
                InternalPin = child.PhysicalPins.Single(p => p.Name == internalName),
            });
        }
        group.TruthTablePinAssignment = Roles();
        return group;
    }

    /// <summary>Extracts one group's truth table at the persisted roles.</summary>
    private static async Task<TruthTable> Extract(TruthTableExtractor extractor, ComponentGroup group)
    {
        var roles = group.TruthTablePinAssignment!;
        return await extractor.ExtractAsync(
            group, roles.InputPinNames, roles.OutputPinNames, roles.BiasPinNames,
            roles.Threshold, WavelengthNm);
    }

    private string WriteGds()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "journey.gds");
        var content = GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef("wg", 0, 0)
            .EndCell()
            .WaveguideCell("wg")
            .EndLibrary()
            .ToArray();
        File.WriteAllBytes(path, content);
        return path;
    }
}

/// <summary>GDS fixture cell builder (same 10×4 µm waveguide shape as GdsImportJourneyFixture).</summary>
file static class ImportedCellJourneyTestCells
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
