using System.Collections.ObjectModel;
using System.Numerics;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Analysis;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;
using CAP_Core.ExternalPorts;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components;

/// <summary>
/// End-to-end multi-chiplet composition journey (issue #927, North-Star probe
/// rung 3→6 of #537), exercised headlessly:
///
///   Step 1: Build chiplet A (splitter + two arm waveguides), group it with
///           exposed pins and store it as a reusable prefab.
///   Step 2: Build chiplet B (combiner + output waveguide) the same way.
///   Step 3: Compose: place one instance of each on a fresh canvas and align
///           them so both pin pairs sit exactly on top of each other
///           (pin-to-pin abutment, standing in for a later edge-coupler pair).
///           Assertion: the router returns valid abutments, no BlockedPath
///           (#923 behaviour on group level).
///   Step 4: Simulate: light injected at chiplet A's input must arrive at
///           chiplet B's output with physically plausible power (S-matrix
///           chain across both group boundaries, value assertion).
///   Step 5: Persist: .lun save → load; both groups, exposed pins, the
///           inter-chiplet connections and the simulation result must survive
///           the round-trip (same output amplitude ± tolerance).
///
/// The chiplets use the same self-contained fixture library as the hierarchy
/// journey (#912): a numeric 50/50 directional coupler and a unity-through
/// waveguide with baked S-matrices, so no bundled-PDK dependency is needed.
/// Chiplet A is the split half of a balanced Mach-Zehnder, chiplet B the
/// recombine half — composed pin-to-pin they must behave like the flat MZI.
/// </summary>
public class MultiChipletCompositionJourneyTests : IDisposable
{
    private const int WavelengthNm = 1550;
    private const double WireGapMicrometers = 5;

    // Same circuit, same solver → deterministic equality across save/load.
    private const double AmplitudeTolerance = 1e-6;

    // Absolute value assertions tolerate the iterative solver's convergence
    // noise (observed ~3e-5 relative on the fixture's lossless ideals).
    private const double SolverValueTolerance = 1e-3;
    private const double PositionTolerance = 1e-9;

    // Fixture geometry: coupler 250x80 µm with left pins (in1, in2) and right pins
    // (out1, out2); waveguide 100x5 µm with pins a0 (left) and b0 (right).
    private const double CouplerWidth = 250;
    private const double WaveguideLength = 100;

    private static readonly string[] ChipletAPinNames =
        { "arm1_b0", "arm2_b0", "split_in1", "split_in2" };
    private static readonly string[] ChipletBPinNames =
        { "combine_in1", "combine_in2", "combine_out1", "det_b0" };

    private readonly ComponentTemplate _couplerTemplate;
    private readonly ComponentTemplate _waveguideTemplate;
    private readonly DesignCanvasViewModel _canvas = new();
    private readonly string _libraryPath;
    private readonly string _designFilePath;

    public MultiChipletCompositionJourneyTests()
    {
        _couplerTemplate = CreateCouplerTemplate();
        _waveguideTemplate = CreateWaveguideTemplate();
        _libraryPath = Path.Combine(Path.GetTempPath(), $"chiplet_library_{Guid.NewGuid():N}");
        _designFilePath = Path.Combine(Path.GetTempPath(), $"chiplet_journey_{Guid.NewGuid():N}.lun");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_libraryPath)) Directory.Delete(_libraryPath, true);
            if (File.Exists(_designFilePath)) File.Delete(_designFilePath);
        }
        catch
        {
            // Temp cleanup must never fail the test run.
        }
    }

    [Fact]
    public async Task MultiChipletJourney_TwoChipletsAbutted_SimulateAndPersist()
    {
        // ── Step 1: Chiplet A — splitter fragment, grouped, stored as prefab ──
        var builderA = new DesignCanvasViewModel();
        var split = Place(builderA, "split", _couplerTemplate, 0, 0);
        var arm1 = Place(builderA, "arm1", _waveguideTemplate, CouplerWidth + WireGapMicrometers, 7.5);
        var arm2 = Place(builderA, "arm2", _waveguideTemplate, CouplerWidth + WireGapMicrometers, 67.5);
        Wire(builderA, GetPin(split, "out1"), GetPin(arm1, "a0"));
        Wire(builderA, GetPin(split, "out2"), GetPin(arm2, "a0"));

        var chipletA = Group(builderA, "Splitter Chiplet", split, arm1, arm2);
        chipletA.ChildComponents.Count.ShouldBe(3, "Step 1: chiplet A owns splitter + two arms");
        chipletA.InternalPaths.Count.ShouldBe(2, "Step 1: the two arm connections freeze into chiplet A");
        chipletA.ExternalPins.Select(p => p.Name).OrderBy(n => n).ShouldBe(
            ChipletAPinNames.OrderBy(n => n),
            "Step 1: chiplet A exposes the two free splitter inputs and both arm ends");

        // ── Step 2: Chiplet B — combiner fragment, grouped, stored as prefab ──
        var builderB = new DesignCanvasViewModel();
        var combine = Place(builderB, "combine", _couplerTemplate, 0, 0);
        var det = Place(builderB, "det", _waveguideTemplate, CouplerWidth + WireGapMicrometers, 67.5);
        Wire(builderB, GetPin(combine, "out2"), GetPin(det, "a0"));

        var chipletB = Group(builderB, "Combiner Chiplet", combine, det);
        chipletB.ChildComponents.Count.ShouldBe(2, "Step 2: chiplet B owns combiner + output waveguide");
        chipletB.InternalPaths.Count.ShouldBe(1, "Step 2: the output connection freezes into chiplet B");
        chipletB.ExternalPins.Select(p => p.Name).OrderBy(n => n).ShouldBe(
            ChipletBPinNames.OrderBy(n => n),
            "Step 2: chiplet B exposes both combiner inputs, the dark port and the output end");

        var saveLibrary = new GroupLibraryManager(_libraryPath);
        var libraryViewModel = new ComponentLibraryViewModel(saveLibrary);
        new SaveGroupAsPrefabCommand(libraryViewModel, new GroupPreviewGenerator(), chipletA, "Splitter Chiplet").Execute();
        new SaveGroupAsPrefabCommand(libraryViewModel, new GroupPreviewGenerator(), chipletB, "Combiner Chiplet").Execute();

        // ── Step 3: Compose both chiplets pin-to-pin on a fresh canvas ────────
        var prefabLibrary = new GroupLibraryManager(_libraryPath);
        prefabLibrary.LoadTemplates();
        var templateA = prefabLibrary.Templates.SingleOrDefault(t => t.Name == "Splitter Chiplet")
            .ShouldNotBeNull("Step 3: chiplet A prefab must survive the library disk round-trip");
        var templateB = prefabLibrary.Templates.SingleOrDefault(t => t.Name == "Combiner Chiplet")
            .ShouldNotBeNull("Step 3: chiplet B prefab must survive the library disk round-trip");

        var instanceA = PlacePrefabInstance(templateA, prefabLibrary, 600, 600);
        var instanceB = PlacePrefabInstance(templateB, prefabLibrary, 2600, 600);
        instanceA.ExternalPins.Select(p => p.Name).OrderBy(n => n).ShouldBe(
            ChipletAPinNames.OrderBy(n => n), "Step 3: chiplet A instance exposes its pins");
        instanceB.ExternalPins.Select(p => p.Name).OrderBy(n => n).ShouldBe(
            ChipletBPinNames.OrderBy(n => n), "Step 3: chiplet B instance exposes its pins");

        // Align chiplet B so its combiner inputs sit exactly on chiplet A's arm
        // ends — both pairs abut at once because the fragments share the 60 µm
        // pin pitch (stand-in for a later edge-coupler pair).
        var aOut1 = ExposedPin(instanceA, "arm1_b0");
        var bIn1 = ExposedPin(instanceB, "combine_in1");
        var (aX, aY) = aOut1.GetAbsolutePosition();
        var (bX, bY) = bIn1.GetAbsolutePosition();
        instanceB.MoveGroup(aX - bX, aY - bY);

        var aOut2 = ExposedPin(instanceA, "arm2_b0");
        var bIn2 = ExposedPin(instanceB, "combine_in2");
        foreach (var (chipletAPin, chipletBPin) in new[] { (aOut1, bIn1), (aOut2, bIn2) })
        {
            var (ax, ay) = chipletAPin.GetAbsolutePosition();
            var (bx, by) = chipletBPin.GetAbsolutePosition();
            bx.ShouldBe(ax, PositionTolerance, "Step 3: abutted pin pair must coincide in X");
            by.ShouldBe(ay, PositionTolerance, "Step 3: abutted pin pair must coincide in Y");
        }

        // #923 on group level: coincident opposing pins route as valid abutments.
        var abutment1 = ConnectAbutment(aOut1, bIn1);
        var abutment2 = ConnectAbutment(aOut2, bIn2);
        _canvas.ConnectionManager.Connections.Count.ShouldBe(2,
            "Step 3: exactly the two inter-chiplet abutments exist");
        new DesignValidator().Validate(_canvas.ConnectionManager.Connections).ShouldBeEmpty(
            "Step 3: pin-to-pin abutments between groups must not raise BlockedPath (#923)");
        abutment1.Connection.IsBlockedFallback.ShouldBeFalse("Step 3: first abutment is a real route");
        abutment2.Connection.IsBlockedFallback.ShouldBeFalse("Step 3: second abutment is a real route");

        // ── Step 4: Simulate — S-matrix chain across both group boundaries ────
        // Split half + recombine half of a balanced MZI: power recombines into
        // the cross port (det arm) and cancels in the through port (combine_out1).
        var laser = InjectLight("source", ExposedPin(instanceA, "split_in1"));
        var fields = await SimulateAsync(_canvas, laser);

        var bright = Amplitude(fields, ExposedPin(instanceB, "det_b0").LogicalPin!.IDOutFlow);
        var dark = Amplitude(fields, ExposedPin(instanceB, "combine_out1").LogicalPin!.IDOutFlow);
        var midChain = Amplitude(fields, aOut1.LogicalPin!.IDOutFlow);

        midChain.ShouldBe(Math.Sqrt(0.5), SolverValueTolerance,
            "Step 4: half the amplitude leaves chiplet A through the upper arm");
        bright.ShouldBe(1.0, SolverValueTolerance,
            "Step 4: the composed system delivers full power at chiplet B's output");
        dark.ShouldBeLessThan(0.01,
            "Step 4: the composed system extinguishes chiplet B's through port");

        // ── Step 5: Persist — groups, pins, abutments and physics survive ─────
        var saveVm = CreateFileOperations(_canvas);
        var saveDialog = new Mock<IFileDialogService>();
        saveDialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_designFilePath);
        saveVm.FileDialogService = saveDialog.Object;
        await saveVm.SaveDesignAsCommand.ExecuteAsync(null);
        File.Exists(_designFilePath).ShouldBeTrue("Step 5: design file must be written");

        var loadedCanvas = new DesignCanvasViewModel();
        var loadVm = CreateFileOperations(loadedCanvas);
        var loadDialog = new Mock<IFileDialogService>();
        loadDialog.Setup(f => f.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_designFilePath);
        loadVm.FileDialogService = loadDialog.Object;
        await loadVm.LoadDesignCommand.ExecuteAsync(null);

        var loadedGroups = loadedCanvas.Components
            .Where(c => c.Component is ComponentGroup)
            .Select(c => (ComponentGroup)c.Component)
            .ToList();
        loadedGroups.Count.ShouldBe(2, "Step 5: both chiplets survive the round-trip");
        var loadedA = loadedGroups.SingleOrDefault(g => g.Identifier == instanceA.Identifier)
            .ShouldNotBeNull("Step 5: chiplet A identity survives");
        var loadedB = loadedGroups.SingleOrDefault(g => g.Identifier == instanceB.Identifier)
            .ShouldNotBeNull("Step 5: chiplet B identity survives");

        loadedA.ExternalPins.Select(p => p.Name).OrderBy(n => n).ShouldBe(
            ChipletAPinNames.OrderBy(n => n), "Step 5: chiplet A keeps its exposed pins");
        loadedB.ExternalPins.Select(p => p.Name).OrderBy(n => n).ShouldBe(
            ChipletBPinNames.OrderBy(n => n), "Step 5: chiplet B keeps its exposed pins");
        foreach (var loadedGroup in loadedGroups)
        {
            foreach (var externalPin in loadedGroup.ExternalPins)
            {
                externalPin.InternalPin!.LogicalPin.ShouldNotBeNull(
                    $"Step 5: exposed pin '{externalPin.Name}' must stay simulatable after load");
            }
        }

        loadedCanvas.Connections.Count.ShouldBe(2, "Step 5: both inter-chiplet abutments survive");
        foreach (var connection in loadedCanvas.Connections)
        {
            var startGroup = connection.Connection.StartPin.ParentComponent?.ParentGroup;
            var endGroup = connection.Connection.EndPin.ParentComponent?.ParentGroup;
            startGroup.ShouldNotBeNull("Step 5: abutment start pin must stay inside a group");
            endGroup.ShouldNotBeNull("Step 5: abutment end pin must stay inside a group");
            ReferenceEquals(startGroup, endGroup).ShouldBeFalse(
                "Step 5: each abutment must keep bridging the two chiplets");
        }

        var (loadedAx, loadedAy) = ExposedPin(loadedA, "arm1_b0").GetAbsolutePosition();
        var (loadedBx, loadedBy) = ExposedPin(loadedB, "combine_in1").GetAbsolutePosition();
        loadedBx.ShouldBe(loadedAx, PositionTolerance, "Step 5: the abutment stays coincident in X");
        loadedBy.ShouldBe(loadedAy, PositionTolerance, "Step 5: the abutment stays coincident in Y");

        var loadedFields = await SimulateAsync(loadedCanvas,
            InjectLight("source", ExposedPin(loadedA, "split_in1")));
        Amplitude(loadedFields, ExposedPin(loadedB, "det_b0").LogicalPin!.IDOutFlow)
            .ShouldBe(bright, AmplitudeTolerance,
                "Step 5: the reloaded system delivers the same output power");
        Amplitude(loadedFields, ExposedPin(loadedB, "combine_out1").LogicalPin!.IDOutFlow)
            .ShouldBeLessThan(0.01, "Step 5: the reloaded system keeps the dark port dark");
    }

    // ── Fixture templates ───────────────────────────────────────────────────────

    private static ComponentTemplate CreateCouplerTemplate() => new()
    {
        Name = "Chiplet Test Coupler",
        Category = "Fixture",
        NazcaFunctionName = "fixture.chiplet.coupler",
        PdkSource = "Chiplet Fixture PDK",
        WidthMicrometers = CouplerWidth,
        HeightMicrometers = 80,
        PinDefinitions = new[]
        {
            new PinDefinition("in1", 0, 10, 180),
            new PinDefinition("in2", 0, 70, 180),
            new PinDefinition("out1", CouplerWidth, 10, 0),
            new PinDefinition("out2", CouplerWidth, 70, 0),
        },
        CreateWavelengthSMatrixMap = pins =>
        {
            // Lossless 50/50 coupler: through = sqrt(1/2) at 0°, cross = sqrt(1/2) at +90°.
            var pinIds = pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();
            var matrix = new SMatrix(pinIds, new List<(Guid, double)>());
            var through = new Complex(Math.Sqrt(0.5), 0);
            var cross = new Complex(0, Math.Sqrt(0.5));
            matrix.SetValues(new Dictionary<(Guid, Guid), Complex>
            {
                { (pins[0].IDInFlow, pins[2].IDOutFlow), through },
                { (pins[0].IDInFlow, pins[3].IDOutFlow), cross },
                { (pins[1].IDInFlow, pins[2].IDOutFlow), cross },
                { (pins[1].IDInFlow, pins[3].IDOutFlow), through },
                { (pins[2].IDInFlow, pins[0].IDOutFlow), through },
                { (pins[2].IDInFlow, pins[1].IDOutFlow), cross },
                { (pins[3].IDInFlow, pins[0].IDOutFlow), cross },
                { (pins[3].IDInFlow, pins[1].IDOutFlow), through },
            });
            return new Dictionary<int, SMatrix> { { WavelengthNm, matrix } };
        },
    };

    private static ComponentTemplate CreateWaveguideTemplate() => new()
    {
        Name = "Chiplet Test Waveguide",
        Category = "Fixture",
        NazcaFunctionName = "fixture.chiplet.waveguide",
        PdkSource = "Chiplet Fixture PDK",
        WidthMicrometers = WaveguideLength,
        HeightMicrometers = 5,
        PinDefinitions = new[]
        {
            new PinDefinition("a0", 0, 2.5, 180),
            new PinDefinition("b0", WaveguideLength, 2.5, 0),
        },
        CreateWavelengthSMatrixMap = pins =>
        {
            var pinIds = pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();
            var matrix = new SMatrix(pinIds, new List<(Guid, double)>());
            matrix.SetValues(new Dictionary<(Guid, Guid), Complex>
            {
                { (pins[0].IDInFlow, pins[1].IDOutFlow), Complex.One },
                { (pins[1].IDInFlow, pins[0].IDOutFlow), Complex.One },
            });
            return new Dictionary<int, SMatrix> { { WavelengthNm, matrix } };
        },
    };

    // ── Journey helpers ─────────────────────────────────────────────────────────

    /// <summary>Places a fixture component on the given canvas with a stable identifier.</summary>
    private Component Place(DesignCanvasViewModel canvas, string identifier, ComponentTemplate template, double x, double y)
    {
        var component = ComponentTemplates.CreateFromTemplate(template, x, y);
        component.Identifier = identifier;
        canvas.AddComponent(component, template.Name);
        return component;
    }

    private static ComponentViewModel ViewModelOf(DesignCanvasViewModel canvas, Component component) =>
        canvas.Components.Single(c => c.Component == component);

    private static PhysicalPin GetPin(Component component, string pinName) =>
        component.PhysicalPins.Single(p => p.Name == pinName);

    /// <summary>The connectable canvas-side pin behind a group's exposed pin.</summary>
    private static PhysicalPin ExposedPin(ComponentGroup group, string pinName) =>
        group.ExternalPins.Single(p => p.Name == pinName).InternalPin!;

    /// <summary>Groups the given components (Ctrl+G equivalent) and returns the group.</summary>
    private static ComponentGroup Group(DesignCanvasViewModel canvas, string name, params Component[] children)
    {
        var command = new CreateGroupCommand(
            canvas, children.Select(c => ViewModelOf(canvas, c)).ToList(), name);
        command.Execute();
        return command.CreatedGroup.ShouldNotBeNull($"grouping '{name}' must succeed");
    }

    /// <summary>
    /// Connects two pins inside one chiplet with an explicit straight route,
    /// frozen so the group captures the deterministic geometry.
    /// </summary>
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

    /// <summary>
    /// Routes two coincident pins through the real router (the #923 abutment
    /// path) and connects them, asserting the route is a valid butt joint.
    /// </summary>
    private WaveguideConnectionViewModel ConnectAbutment(PhysicalPin from, PhysicalPin to)
    {
        var path = _canvas.Router.Route(from, to);
        path.IsBlockedFallback.ShouldBeFalse(
            $"Step 3: abutment {from.Name} -> {to.Name} must not fall back to a blocked route");
        path.IsValid.ShouldBeTrue($"Step 3: abutment {from.Name} -> {to.Name} must be valid");
        path.Segments.Count.ShouldBe(1,
            $"Step 3: abutment {from.Name} -> {to.Name} must be a single butt joint");
        var connection = _canvas.ConnectPinsWithCachedRoute(from, to, path);
        connection.ShouldNotBeNull($"Step 3: abutment {from.Name} -> {to.Name} must be created");
        connection!.Connection.IsRouteFrozen = true;
        return connection;
    }

    /// <summary>Places one prefab instance via the library placement command and returns it.</summary>
    private ComponentGroup PlacePrefabInstance(
        GroupTemplate template, GroupLibraryManager library, double centerX, double centerY)
    {
        var before = _canvas.Components.ToHashSet();
        var command = PlaceGroupTemplateCommand.TryCreate(
            _canvas, library, template, centerX, centerY, out var physicsRejection);
        physicsRejection.ShouldBeNull("Step 3: the saved prefab must pass the passivity guard");
        command.ShouldNotBeNull("Step 3: prefab placement command must be creatable");
        command!.Execute();
        return _canvas.Components.First(c => !before.Contains(c)).Component
            .ShouldBeOfType<ComponentGroup>("Step 3: placement adds exactly one group to the canvas");
    }

    private static (ExternalInput Input, Guid PinIdInFlow) InjectLight(string name, PhysicalPin pin) =>
        (new ExternalInput(name, new LaserType(LightColor.Red), 0, new Complex(1.0, 0), true),
         pin.LogicalPin!.IDInFlow);

    /// <summary>Runs the S-matrix field propagation over everything currently on the canvas.</summary>
    private static async Task<Dictionary<Guid, Complex>> SimulateAsync(
        DesignCanvasViewModel canvas, params (ExternalInput Input, Guid PinIdInFlow)[] inputs)
    {
        var portManager = new PhysicalExternalPortManager();
        foreach (var (input, pinIdInFlow) in inputs)
        {
            portManager.AddLightSource(input, pinIdInFlow);
        }

        var tileManager = new ComponentListTileManager();
        foreach (var viewModel in canvas.Components)
        {
            tileManager.AddComponent(viewModel.Component);
        }

        var grid = GridManager.CreateForSimulation(tileManager, canvas.ConnectionManager, portManager);
        var calculator = new GridLightCalculator(new SystemMatrixBuilder(grid), grid);
        return await calculator.CalculateFieldPropagationAsync(new CancellationTokenSource(), WavelengthNm);
    }

    private static double Amplitude(Dictionary<Guid, Complex> fields, Guid pinFlow) =>
        fields.TryGetValue(pinFlow, out var value)
            ? value.Magnitude
            : throw new ShouldAssertException($"pin flow {pinFlow} missing from simulated fields");

    /// <summary>Creates the file-operations facade used for the .lun save/load round-trip.</summary>
    private FileOperationsViewModel CreateFileOperations(DesignCanvasViewModel canvas)
    {
        var library = new ObservableCollection<ComponentTemplate> { _couplerTemplate, _waveguideTemplate };
        return new FileOperationsViewModel(
            canvas,
            new CommandManager(),
            new SimpleNazcaExporter(),
            new CAP_Core.Export.SaxExporter(),
            library,
            new GdsExportViewModel(new CAP_Core.Export.GdsExportService()),
            new CAP.Avalonia.ViewModels.Export.PhotonTorchExportViewModel(
                new CAP_Core.Export.PhotonTorchExporter(), canvas),
            null!,
            errorConsole: new CAP_Core.ErrorConsoleService());
    }
}
