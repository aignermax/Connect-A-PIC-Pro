using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Moq;
using Shouldly;
using UnitTests.Analysis.LogicAnalysis;
using UnitTests.Helpers;
using UnitTests.Import.Gds;

namespace UnitTests.Integration;

/// <summary>
/// Journey mechanics for <see cref="ImportedGdsCellLogicGateSeamTests"/> (issue #1087):
/// headless GDS import through the real <see cref="GdsImportService"/> on the
/// MainViewModel's design scope, the OR-gate circuit build with the imported cell
/// spliced into the combiner→output signal path, the real Ctrl+G grouping, the Truth
/// Table panel extraction, and the .lun save/reload through the real file operations.
/// Import harness mirrors <c>GdsImportDesignRoundTripTests</c>; panel, grouping, and
/// save/load wiring mirror <c>NandGameTruthTableJourneyTests</c>.
/// </summary>
public sealed class ImportedGdsCellLogicGateSeamJourney : IDisposable
{
    /// <summary>Truth-table threshold used throughout the journey (same as the NAND-game journey's OR gate).</summary>
    public const double Threshold = 0.25;

    /// <summary>Extraction wavelength: no light source on the canvas → the panel's red-laser fallback.</summary>
    public const int WavelengthNm = 1550;

    private const double WireGapMicrometers = 5;

    // Pin-line geometry: the combiner's out1 port sits at local y = 10, the
    // imported cell's in/out pins at local y = 2, the output waveguide's a0 at
    // local y = 2.5 — placing components so all splice pins share y = 10.
    private const double CombinerOutPinLineY = 10;
    private const double ImportedPinOffsetY = 2;
    private const double WaveguidePinOffsetY = 2.5;

    /// <summary>Component identifiers — the group names its external pins after them.</summary>
    public const string CombinerIdentifier = "combine";
    public const string OutputIdentifier = "out";
    public const string ImportedIdentifier = "imported";
    public const string ImportedCellName = "wg";
    public const string GroupName = "OR Gate";
    public const string InputPinA = "combine_in1";
    public const string InputPinB = "combine_in2";
    public const string OutputPinY = "out_b0";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-gds-logic-seam-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch
        {
            // Temp cleanup must never fail the test run.
        }
    }

    /// <summary>Creates the real MainViewModel wiring with the fixture templates registered.</summary>
    public static MainViewModel CreateMainViewModel(DesignCanvasViewModel canvas)
    {
        var mainVm = MainViewModelTestHelper.CreateMainViewModel(canvas: canvas);
        // FileOperations resolves saved components against LeftPanel.AllTemplates —
        // the fixture templates must be registered there for the .lun round-trip.
        mainVm.LeftPanel.AllTemplates.Add(LogicGateFixtureFactory.CreateCombinerTemplate());
        mainVm.LeftPanel.AllTemplates.Add(LogicGateFixtureFactory.CreateWaveguideTemplate());
        return mainVm;
    }

    /// <summary>Imports the 2-pin GDS cell through the real service on the VM's design scope.</summary>
    public async Task<ComponentTemplate> ImportCellAsync(MainViewModel mainVm)
    {
        var gdsPath = WriteGds();
        var designScope = mainVm.FileOperations.DesignScopedGdsComponents.ShouldNotBeNull(
            "the MainViewModel wiring carries the design-scoped GDS store");
        var service = new GdsImportService(designScope, () => mainVm.LeftPanel.AllTemplates.ToList());
        var outcome = await service.ImportAsync(gdsPath, "TOP", null, null);
        outcome.Warnings.ShouldBeEmpty("the fixture cell imports cleanly");
        return mainVm.LeftPanel.AllTemplates.Single(t =>
            t.Name == ImportedCellName && t.PdkSource == outcome.UserPdkName);
    }

    /// <summary>
    /// Builds the OR-gate circuit: combiner → output waveguide. With
    /// <paramref name="splice"/> the imported cell sits in the combiner→output
    /// signal path (combiner.out1 → imported.in, imported.out → waveguide.a0).
    /// The unspliced reference wires the same TOTAL length (2 × WireGap) as the
    /// spliced layout's two hops, so the neutrality comparison is exact under
    /// any propagation loss — only the imported cell's S-matrix can differ.
    /// </summary>
    public static void BuildOrGateCircuit(
        DesignCanvasViewModel canvas, ComponentTemplate? importedTemplate, bool splice)
    {
        var combinerTemplate = LogicGateFixtureFactory.CreateCombinerTemplate();
        var waveguideTemplate = LogicGateFixtureFactory.CreateWaveguideTemplate();

        var combiner = ComponentTemplates.CreateFromTemplate(combinerTemplate, 0, 0);
        combiner.Identifier = CombinerIdentifier;
        canvas.AddComponent(combiner, combinerTemplate.Name);

        // Combiner out1 sits at absolute (250, 10); keep every splice pin on that line.
        Component? imported = null;
        var outputX = combinerTemplate.WidthMicrometers + 2 * WireGapMicrometers;
        if (splice)
        {
            imported = ComponentTemplates.CreateFromTemplate(importedTemplate!,
                combinerTemplate.WidthMicrometers + WireGapMicrometers,
                CombinerOutPinLineY - ImportedPinOffsetY);
            imported.Identifier = ImportedIdentifier;
            canvas.AddComponent(imported, importedTemplate!.Name, importedTemplate.PdkSource);
            outputX += imported.WidthMicrometers;
        }

        var output = ComponentTemplates.CreateFromTemplate(waveguideTemplate, outputX,
            CombinerOutPinLineY - WaveguidePinOffsetY);
        output.Identifier = OutputIdentifier;
        canvas.AddComponent(output, waveguideTemplate.Name);

        if (imported == null)
        {
            Wire(canvas, Pin(combiner, "out1"), Pin(output, "a0"));
            return;
        }
        Wire(canvas, Pin(combiner, "out1"), Pin(imported, "in"));
        Wire(canvas, Pin(imported, "out"), Pin(output, "a0"));
    }

    /// <summary>Groups the whole canvas via the real Ctrl+G command.</summary>
    public static ComponentGroup GroupCircuit(DesignCanvasViewModel canvas)
    {
        var command = new CreateGroupCommand(canvas, canvas.Components.ToList(), GroupName);
        command.Execute();
        return command.CreatedGroup.ShouldNotBeNull("grouping the circuit must create a group");
    }

    /// <summary>Selects the group through the real selection-changed wiring of MainViewModel.</summary>
    public static void SelectGroup(MainViewModel mainVm, DesignCanvasViewModel canvas, ComponentGroup group)
    {
        var groupViewModel = canvas.Components.Single(c => c.Component == group);
        canvas.Selection.SelectSingle(groupViewModel);
        mainVm.CanvasInteraction.SelectedComponent = groupViewModel;
    }

    /// <summary>Checks a + b as inputs and y as output, then extracts at the journey threshold.</summary>
    public static async Task ExtractOrTable(MainViewModel mainVm)
    {
        var truthTable = mainVm.RightPanel.TruthTable;
        truthTable.InputPins.Single(p => p.PinName == InputPinA).IsChecked = true;
        truthTable.InputPins.Single(p => p.PinName == InputPinB).IsChecked = true;
        truthTable.OutputPins.Single(p => p.PinName == OutputPinY).IsChecked = true;
        truthTable.Threshold = Threshold;
        await truthTable.ExtractCommand.ExecuteAsync(null);
    }

    /// <summary>Saves through the real file operations and returns the design path.</summary>
    public async Task<string> Save(MainViewModel mainVm)
    {
        var path = Path.Combine(_root, "seam.lun");
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(path);
        mainVm.FileDialogService = dialog.Object;
        await mainVm.FileOperations.SaveDesignAsCommand.ExecuteAsync(null);
        File.Exists(path).ShouldBeTrue("the design file must be written");
        return path;
    }

    /// <summary>Reloads a saved design through the real load path into a fresh MainViewModel.</summary>
    public static async Task<(MainViewModel FreshVm, DesignCanvasViewModel FreshCanvas)> Reload(
        string path)
    {
        var freshCanvas = new DesignCanvasViewModel();
        var freshVm = CreateMainViewModel(freshCanvas);
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(path);
        freshVm.FileDialogService = dialog.Object;
        await freshVm.FileOperations.LoadDesignCommand.ExecuteAsync(null);
        return (freshVm, freshCanvas);
    }

    /// <summary>Connects two pins with an explicit straight route, frozen for determinism.</summary>
    private static void Wire(DesignCanvasViewModel canvas, PhysicalPin from, PhysicalPin to)
    {
        var (x1, y1) = from.GetAbsolutePosition();
        var (x2, y2) = to.GetAbsolutePosition();
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(x1, y1, x2, y2, 0));
        var connection = canvas.ConnectPinsWithCachedRoute(from, to, path);
        connection.ShouldNotBeNull($"route {from.Name} -> {to.Name} must be created");
        connection!.Connection.IsRouteFrozen = true;
    }

    private static PhysicalPin Pin(Component component, string pinName) =>
        component.PhysicalPins.Single(p => p.Name == pinName);

    /// <summary>TOP with one 10×4 µm gdsfactory-style waveguide cell (pins in/out).</summary>
    private string WriteGds()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "seam.gds");
        File.WriteAllBytes(path, GdsTestWriter.Create()
            .StandardPrologue()
            .BeginCell("TOP")
                .SRef(ImportedCellName, 0, 0)
            .EndCell()
            .BeginCell(ImportedCellName)
                .Boundary(1, 0, (0, 1750), (10000, 1750), (10000, 2250), (0, 2250), (0, 1750))
                .Boundary(111, 0, (0, 0), (10000, 0), (10000, 4000), (0, 4000), (0, 0))
                .Text(1, 10, "in", 0, 2000)
                .Text(1, 10, "out", 10000, 2000)
            .EndCell()
            .EndLibrary()
            .ToArray());
        return path;
    }
}
