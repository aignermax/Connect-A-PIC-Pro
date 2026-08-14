using System.Collections.ObjectModel;
using System.Numerics;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
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
/// End-to-end hierarchy journey (rung 3 of the roadmap, issue #905), exercised headlessly:
///
///   Step 1: Place a small interferometer (2 couplers + 2 waveguides) and verify
///           simulation output at one port (bright/dark ports of a balanced MZI).
///   Step 2: Group it (the Ctrl+G equivalent, <see cref="CreateGroupCommand"/>), assert the
///           exposed external pins exist and are connectable.
///   Step 3: Save the group as a prefab, place the prefab twice side by side, route both
///           instances to separate inputs/outputs.
///   Step 4: Simulate; assert both instances produce the same transfer function
///           independently (and identical to the pre-group flat circuit).
///   Step 5: Ungroup one instance; assert the other instance and its routes stay intact.
///   Step 6: Save/load the whole design; assert groups, prefab instances, pins and
///           connections survive.
///
/// The circuit uses a small self-contained fixture library (no bundled-PDK dependency):
/// a numeric 50/50 directional coupler and a unity-through waveguide — the exact
/// equivalent of the demo PDK's parametric cells, but with baked S-matrices so the
/// prefab serialization round-trip is exercised on the same data path.
/// </summary>
public class GroupReuseHierarchyJourneyTests : IDisposable
{
    private const int WavelengthNm = 1550;
    private const double WireGapMicrometers = 5;
    private const double AmplitudeTolerance = 1e-6;

    // Fixture geometry: coupler 250x80 µm with left pins (in1, in2) and right pins
    // (out1, out2); waveguide 100x5 µm with pins a0 (left) and b0 (right).
    private const double CouplerWidth = 250;
    private const double WaveguideLength = 100;
    private const double WaveguidePinOffsetY = 2.5;

    private static readonly string[] GroupPinNames =
        { "combine_out1", "combine_out2", "split_in1", "split_in2" };

    private readonly ComponentTemplate _couplerTemplate;
    private readonly ComponentTemplate _waveguideTemplate;
    private readonly DesignCanvasViewModel _canvas = new();
    private readonly string _libraryPath;
    private readonly string _designFilePath;

    public GroupReuseHierarchyJourneyTests()
    {
        _couplerTemplate = CreateCouplerTemplate();
        _waveguideTemplate = CreateWaveguideTemplate();
        _libraryPath = Path.Combine(Path.GetTempPath(), $"mzi_library_{Guid.NewGuid():N}");
        _designFilePath = Path.Combine(Path.GetTempPath(), $"mzi_journey_{Guid.NewGuid():N}.lun");
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
    public async Task HierarchyJourney_GroupReuseTwice_SimulateUngroupAndPersist()
    {
        // ── Step 1: Build the interferometer and verify simulation output ───────
        // Balanced Mach-Zehnder: split → two equal arms → combine. With the 50/50
        // coupler convention (cross port gets +90°), power recombines into the
        // cross port ("out2") and cancels in the through port ("out1").
        var split = Place("split", _couplerTemplate, 0, 0);
        var armA = Place("arm1", _waveguideTemplate, CouplerWidth + WireGapMicrometers, 7.5);
        var armB = Place("arm2", _waveguideTemplate, CouplerWidth + WireGapMicrometers, 67.5);
        var combine = Place("combine", _couplerTemplate,
            CouplerWidth + 2 * WireGapMicrometers + WaveguideLength, 0);

        Place("src", _waveguideTemplate, -WaveguideLength - WireGapMicrometers, 7.5);
        Place("det", _waveguideTemplate,
            2 * CouplerWidth + WaveguideLength + 3 * WireGapMicrometers, 67.5);

        Wire(GetPin(split, "out1"), GetPin(armA, "a0"));
        Wire(GetPin(armA, "b0"), GetPin(combine, "in1"));
        Wire(GetPin(split, "out2"), GetPin(armB, "a0"));
        Wire(GetPin(armB, "b0"), GetPin(combine, "in2"));
        Wire(GetPin(FindComponent("src"), "b0"), GetPin(split, "in1"));
        Wire(GetPin(combine, "out2"), GetPin(FindComponent("det"), "a0"));

        var laser = InjectLight("source", GetPin(FindComponent("src"), "a0"));
        var flat = await SimulateAsync(laser);

        var flatBright = Amplitude(flat, GetPin(FindComponent("det"), "b0").LogicalPin!.IDOutFlow);
        var flatDark = Amplitude(flat, GetPin(combine, "out1").LogicalPin!.IDOutFlow);

        flatBright.ShouldBeGreaterThan(0.8,
            "Step 1: balanced MZI must deliver (almost) full power at the cross port");
        flatDark.ShouldBeLessThan(0.05,
            "Step 1: balanced MZI must extinguish the through port (destructive interference)");

        // ── Step 2: Group the interferometer (Ctrl+G equivalent) ───────────────
        var createGroup = new CreateGroupCommand(
            _canvas,
            new List<ComponentViewModel>
            {
                ViewModelOf(split), ViewModelOf(armA), ViewModelOf(armB), ViewModelOf(combine)
            },
            "MZI Block");
        createGroup.Execute();

        var group = createGroup.CreatedGroup;
        group.ShouldNotBeNull("Step 2: Ctrl+G on the four components must create a group");
        _canvas.Components.Count.ShouldBe(3,
            "Step 2: canvas should hold src, det and the group after grouping");
        group!.ChildComponents.Count.ShouldBe(4, "Step 2: group must own the four children");
        group.InternalPaths.Count.ShouldBe(4,
            "Step 2: the four arm connections must be frozen as internal paths");

        group.ExternalPins.Select(p => p.Name).OrderBy(n => n).ShouldBe(
            GroupPinNames.OrderBy(n => n),
            "Step 2: the four unoccupied coupler ports must be exposed as external pins");
        foreach (var externalPin in group.ExternalPins)
        {
            externalPin.InternalPin.ShouldNotBeNull(
                $"Step 2: pin '{externalPin.Name}' needs an internal pin");
            externalPin.InternalPin!.LogicalPin.ShouldNotBeNull(
                $"Step 2: pin '{externalPin.Name}' needs a logical pin to stay simulatable");
            _canvas.AllPins.Any(p => p.Pin == externalPin.InternalPin).ShouldBeTrue(
                $"Step 2: exposed pin '{externalPin.Name}' must be visible/connectable on canvas");
        }

        // Connectability proof: hook a probe waveguide to the still-free "split_in2" port.
        var probeTarget = group.ExternalPins.Single(p => p.Name == "split_in2").InternalPin!;
        var (probePinX, probePinY) = probeTarget.GetAbsolutePosition();
        Place("probe", _waveguideTemplate,
            probePinX - WaveguideLength - WireGapMicrometers, probePinY - WaveguidePinOffsetY);
        Wire(GetPin(FindComponent("probe"), "b0"), probeTarget);

        // Grouping must not change the physics: same transfer function as the flat circuit.
        foreach (var frozenPath in group.InternalPaths)
        {
            frozenPath.Path.Segments.Count.ShouldBe(1,
                "Step 2: frozen internal routes must keep their captured geometry");
        }
        var grouped = await SimulateAsync(laser);
        Amplitude(grouped, GetPin(FindComponent("det"), "b0").LogicalPin!.IDOutFlow)
            .ShouldBe(flatBright, AmplitudeTolerance,
                "Step 2: grouping must not change the transfer function");

        // ── Step 3: Save as prefab, instantiate twice, route separately ─────────
        var saveLibrary = new GroupLibraryManager(_libraryPath);
        var libraryViewModel = new ComponentLibraryViewModel(saveLibrary);
        new SaveGroupAsPrefabCommand(libraryViewModel, new GroupPreviewGenerator(), group, "MZI Block").Execute();
        group.IsPrefab.ShouldBeTrue("Step 3: saving marks the group as prefab");

        // Reload the library from disk (simulates reusing the prefab in a later session).
        var prefabLibrary = new GroupLibraryManager(_libraryPath);
        prefabLibrary.LoadTemplates();
        var template = prefabLibrary.Templates.SingleOrDefault(t => t.Name == "MZI Block")
            .ShouldNotBeNull("Step 3: prefab must survive the library disk round-trip");

        var instanceA = PlacePrefabInstance(template, prefabLibrary, 600, 600);
        var instanceB = PlacePrefabInstance(template, prefabLibrary, 1700, 600);
        ReferenceEquals(instanceA, instanceB).ShouldBeFalse(
            "Step 3: two independent prefab instances");

        foreach (var (instance, index) in new[] { (instanceA, 1), (instanceB, 2) })
        {
            instance.ChildComponents.Count.ShouldBe(4,
                $"Step 3: prefab instance {index} must own the four MZI children");
            instance.ExternalPins.Count.ShouldBe(4,
                $"Step 3: prefab instance {index} must expose the four external pins");

            var inputPin = instance.ExternalPins.Single(p => p.Name == "split_in1").InternalPin!;
            var (inX, inY) = inputPin.GetAbsolutePosition();
            Place($"src{index}", _waveguideTemplate,
                inX - WaveguideLength - WireGapMicrometers, inY - WaveguidePinOffsetY);
            Wire(GetPin(FindComponent($"src{index}"), "b0"), inputPin);

            var outputPin = instance.ExternalPins.Single(p => p.Name == "combine_out2").InternalPin!;
            var (outX, outY) = outputPin.GetAbsolutePosition();
            Place($"det{index}", _waveguideTemplate,
                outX + WireGapMicrometers, outY - WaveguidePinOffsetY);
            Wire(outputPin, GetPin(FindComponent($"det{index}"), "a0"));
        }

        // ── Step 4: Simulate — same transfer function, independent state ────────
        var laserA = InjectLight("sourceA", GetPin(FindComponent("src1"), "a0"));
        var laserB = InjectLight("sourceB", GetPin(FindComponent("src2"), "a0"));
        var fields = await SimulateAsync(laser, laserA, laserB);

        var originalOut = Amplitude(fields, GetPin(FindComponent("det"), "b0").LogicalPin!.IDOutFlow);
        var instanceAOut = Amplitude(fields, GetPin(FindComponent("det1"), "b0").LogicalPin!.IDOutFlow);
        var instanceBOut = Amplitude(fields, GetPin(FindComponent("det2"), "b0").LogicalPin!.IDOutFlow);

        originalOut.ShouldBe(flatBright, AmplitudeTolerance,
            "Step 4: the grouped original must still transfer like the flat circuit");
        instanceAOut.ShouldBe(flatBright, AmplitudeTolerance,
            "Step 4: prefab instance A must transfer like the flat circuit");
        instanceBOut.ShouldBe(flatBright, AmplitudeTolerance,
            "Step 4: prefab instance B must transfer like the flat circuit");

        // No crosstalk: the two instances are deep copies with disjoint identities.
        var idsA = instanceA.GetAllComponentsRecursive().Select(c => c.Id).ToHashSet();
        instanceB.GetAllComponentsRecursive().Any(c => idsA.Contains(c.Id)).ShouldBeFalse(
            "Step 4: prefab instances must not share component identities");
        GetPin(FindComponent("det1"), "b0").LogicalPin!.IDOutFlow
            .ShouldNotBe(GetPin(FindComponent("det2"), "b0").LogicalPin!.IDOutFlow,
                "Step 4: instance terminal pins must have independent flow identities");

        // ── Step 5: Ungroup instance A; instance B and its routes stay intact ───
        new UngroupCommand(_canvas, instanceA).Execute();

        var remainingGroups = _canvas.Components
            .Where(c => c.Component is ComponentGroup)
            .Select(c => (ComponentGroup)c.Component)
            .ToList();
        remainingGroups.Count.ShouldBe(2,
            "Step 5: only the original group and instance B remain grouped");
        remainingGroups.ShouldContain(instanceB, "Step 5: instance B must survive instance A's ungroup");
        instanceB.ChildComponents.Count.ShouldBe(4, "Step 5: instance B keeps its children");
        instanceB.InternalPaths.Count.ShouldBe(4, "Step 5: instance B keeps its frozen routes");
        instanceB.ExternalPins.Count.ShouldBe(4, "Step 5: instance B keeps its external pins");

        var instanceBIds = instanceB.GetAllComponentsRecursive().Select(c => c.Id).ToHashSet();
        _canvas.ConnectionManager.Connections
            .Count(c => instanceBIds.Contains(c.StartPin.ParentComponent!.Id)
                        || instanceBIds.Contains(c.EndPin.ParentComponent!.Id))
            .ShouldBe(2, "Step 5: both routes into instance B must survive");

        foreach (var idPrefix in new[] { "split", "arm1", "arm2", "combine" })
        {
            _canvas.Components.Any(c => c.Component.Identifier.StartsWith(idPrefix)
                                        && c.Component.ParentGroup == null)
                .ShouldBeTrue($"Step 5: ungrouped '{idPrefix}' must sit top-level on the canvas");
        }
        _canvas.ConnectionManager.Connections.Count(IsInternalMziRoute).ShouldBe(4,
            "Step 5: ungrouping must restore the four internal arm connections as live routes");

        var afterUngroup = await SimulateAsync(laser, laserA, laserB);
        Amplitude(afterUngroup, GetPin(FindComponent("det2"), "b0").LogicalPin!.IDOutFlow)
            .ShouldBe(flatBright, AmplitudeTolerance,
                "Step 5: instance B keeps simulating identically after instance A's ungroup");
        Amplitude(afterUngroup, GetPin(FindComponent("det1"), "b0").LogicalPin!.IDOutFlow)
            .ShouldBe(flatBright, AmplitudeTolerance,
                "Step 5: the exploded instance keeps working as flat components");

        // ── Step 6: Save/load the whole design — everything survives ────────────
        var componentsBefore = _canvas.Components.Count;
        var connectionsBefore = _canvas.ConnectionManager.Connections.Count;

        var saveVm = CreateFileOperations(_canvas);
        var saveDialog = new Mock<IFileDialogService>();
        saveDialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_designFilePath);
        saveVm.FileDialogService = saveDialog.Object;
        await saveVm.SaveDesignAsCommand.ExecuteAsync(null);
        File.Exists(_designFilePath).ShouldBeTrue("Step 6: design file must be written");

        var loadedCanvas = new DesignCanvasViewModel();
        var loadVm = CreateFileOperations(loadedCanvas);
        var loadDialog = new Mock<IFileDialogService>();
        loadDialog.Setup(f => f.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_designFilePath);
        loadVm.FileDialogService = loadDialog.Object;
        await loadVm.LoadDesignCommand.ExecuteAsync(null);

        loadedCanvas.Components.Count.ShouldBe(componentsBefore,
            "Step 6: all components (standalone, groups, exploded prefab children) survive");
        loadedCanvas.Connections.Count.ShouldBe(connectionsBefore,
            "Step 6: all connections survive, including routes into prefab pins");

        var loadedGroups = loadedCanvas.Components
            .Where(c => c.Component is ComponentGroup)
            .Select(c => (ComponentGroup)c.Component)
            .ToList();
        loadedGroups.Count.ShouldBe(2, "Step 6: original group and prefab instance B survive");
        loadedGroups.Select(g => g.Identifier).OrderBy(i => i)
            .ShouldBe(new[] { group.Identifier, instanceB.Identifier }.OrderBy(i => i),
                "Step 6: group identities survive the round-trip");

        foreach (var loadedGroup in loadedGroups)
        {
            loadedGroup.ChildComponents.Count.ShouldBe(4,
                $"Step 6: group '{loadedGroup.GroupName}' keeps its children");
            loadedGroup.InternalPaths.Count.ShouldBe(4,
                $"Step 6: group '{loadedGroup.GroupName}' keeps its internal routes");
            loadedGroup.ExternalPins.Select(p => p.Name).OrderBy(n => n)
                .ShouldBe(GroupPinNames.OrderBy(n => n),
                    $"Step 6: group '{loadedGroup.GroupName}' keeps its exposed pins");
            foreach (var externalPin in loadedGroup.ExternalPins)
            {
                externalPin.InternalPin!.LogicalPin.ShouldNotBeNull(
                    $"Step 6: exposed pin '{externalPin.Name}' must stay simulatable after load");
            }
        }

        // The exploded prefab copy survived as standalone, template-backed components.
        var standaloneIdentifiers = loadedCanvas.Components
            .Where(c => c.Component is not ComponentGroup)
            .Select(c => c.Component.Identifier)
            .ToList();
        foreach (var idPrefix in new[] { "split", "combine", "arm1", "arm2" })
        {
            standaloneIdentifiers.Any(id => id.StartsWith(idPrefix, StringComparison.Ordinal))
                .ShouldBeTrue($"Step 6: ungrouped '{idPrefix}' must survive save/load");
        }

        // Routes landing on prefab-exposed pins must resolve again after load.
        var routeIntoInstance = loadedCanvas.Connections.SingleOrDefault(c =>
            c.Connection.StartPin.ParentComponent?.Identifier == "src2");
        routeIntoInstance.ShouldNotBeNull(
            "Step 6: the route from src2 into instance B survives");
        routeIntoInstance!.Connection.EndPin.ParentComponent.ParentGroup.ShouldNotBeNull(
            "Step 6: the src2 route must land on a pin inside the loaded prefab instance");
    }

    // ── Fixture templates ───────────────────────────────────────────────────────

    private static ComponentTemplate CreateCouplerTemplate() => new()
    {
        Name = "MZI Test Coupler",
        Category = "Fixture",
        NazcaFunctionName = "fixture.mzi.coupler",
        PdkSource = "MZI Fixture PDK",
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
        Name = "MZI Test Waveguide",
        Category = "Fixture",
        NazcaFunctionName = "fixture.mzi.waveguide",
        PdkSource = "MZI Fixture PDK",
        WidthMicrometers = WaveguideLength,
        HeightMicrometers = 5,
        PinDefinitions = new[]
        {
            new PinDefinition("a0", 0, WaveguidePinOffsetY, 180),
            new PinDefinition("b0", WaveguideLength, WaveguidePinOffsetY, 0),
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

    /// <summary>Places a fixture component on the journey canvas with a stable identifier.</summary>
    private Component Place(string identifier, ComponentTemplate template, double x, double y)
    {
        var component = ComponentTemplates.CreateFromTemplate(template, x, y);
        component.Identifier = identifier;
        _canvas.AddComponent(component, template.Name);
        return component;
    }

    /// <summary>Finds a top-level canvas component by its stable identifier.</summary>
    private Component FindComponent(string identifier) =>
        _canvas.Components.Single(c => c.Component.Identifier == identifier).Component;

    private ComponentViewModel ViewModelOf(Component component) =>
        _canvas.Components.Single(c => c.Component == component);

    private static PhysicalPin GetPin(Component component, string pinName) =>
        component.PhysicalPins.Single(p => p.Name == pinName);

    /// <summary>
    /// Connects two pins with an explicit straight route, frozen so later
    /// re-route passes keep the deterministic geometry.
    /// </summary>
    private void Wire(PhysicalPin from, PhysicalPin to)
    {
        var (x1, y1) = from.GetAbsolutePosition();
        var (x2, y2) = to.GetAbsolutePosition();
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(x1, y1, x2, y2, 0));
        var connection = _canvas.ConnectPinsWithCachedRoute(from, to, path);
        connection.ShouldNotBeNull($"route {from.Name} -> {to.Name} must be created");
        connection!.Connection.IsRouteFrozen = true;
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
    private async Task<Dictionary<Guid, Complex>> SimulateAsync(
        params (ExternalInput Input, Guid PinIdInFlow)[] inputs)
    {
        var portManager = new PhysicalExternalPortManager();
        foreach (var (input, pinIdInFlow) in inputs)
        {
            portManager.AddLightSource(input, pinIdInFlow);
        }

        var tileManager = new ComponentListTileManager();
        foreach (var viewModel in _canvas.Components)
        {
            tileManager.AddComponent(viewModel.Component);
        }

        var grid = GridManager.CreateForSimulation(tileManager, _canvas.ConnectionManager, portManager);
        var calculator = new GridLightCalculator(new SystemMatrixBuilder(grid), grid);
        return await calculator.CalculateFieldPropagationAsync(new CancellationTokenSource(), WavelengthNm);
    }

    private static double Amplitude(Dictionary<Guid, Complex> fields, Guid pinFlow) =>
        fields.TryGetValue(pinFlow, out var value)
            ? value.Magnitude
            : throw new ShouldAssertException($"pin flow {pinFlow} missing from simulated fields");

    private static bool IsInternalMziRoute(CAP_Core.Components.Connections.WaveguideConnection connection) =>
        connection.StartPin.ParentComponent is { } start
        && connection.EndPin.ParentComponent is { } end
        && start.ParentGroup is null && end.ParentGroup is null
        && IsMziPart(start.Identifier) && IsMziPart(end.Identifier);

    private static bool IsMziPart(string identifier) =>
        new[] { "split", "arm1", "arm2", "combine" }
            .Any(prefix => identifier.StartsWith(prefix, StringComparison.Ordinal));

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
