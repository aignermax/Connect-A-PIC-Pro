using CAP_Core.Components;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.ExternalPorts;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;
using System.Numerics;
using UnitTests.Simulation;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Tests that light simulation values remain identical after flattening groups and prefab instances.
/// Verifies that grouping/ungrouping is a purely organizational operation with no simulation effect.
/// Covers issue #319: groups must not "swallow" light during simulation.
/// </summary>
[Trait("Category", "Slow")]
public class FlattenGroupLightPreservationTests
{
    private static readonly int[] AllWavelengths = { StandardWaveLengths.RedNM };
    private const int SimWavelength = 1550; // StandardWaveLengths.RedNM
    private const double LightMatchTolerance = 1e-10;

    /// <summary>
    /// Comprehensive test with >=5 element types: GC source, standalone waveguide,
    /// regular ComponentGroup, prefab ComponentGroup, GC output, and internal waveguides.
    /// Design: GC_src -> standalone_wg -> regularGroup(wg1->wg2) -> prefabGroup(wg3->wg4) -> GC_out
    /// </summary>
    [Fact]
    public async Task FlattenAllGroups_LightValuesIdenticalBeforeAndAfter()
    {
        // Arrange - build design with >=5 element types
        var gcSrc = IntegrationCircuitBuilder.CreateGratingCoupler("GC_src", 0, 0, AllWavelengths);
        var standaloneWg = CreateWaveguideInfo("StandaloneWG", 40, 0);
        var wg1 = CreateWaveguideInfo("WG1", 80, 0);
        var wg2 = CreateWaveguideInfo("WG2", 110, 0);
        var wg3 = CreateWaveguideInfo("WG3", 160, 0);
        var wg4 = CreateWaveguideInfo("WG4", 190, 0);
        var gcOut = IntegrationCircuitBuilder.CreateGratingCoupler("GC_out", 230, 0, AllWavelengths);

        var regularGroup = BuildGroup("RegularGroup", wg1, wg2, isPrefab: false);
        var prefabGroup = BuildGroup("PrefabGroup", wg3, wg4, isPrefab: true);

        // --- Run simulation BEFORE flatten ---
        var tilesBefore = new ComponentListTileManager();
        foreach (var comp in new[] { gcSrc.Component, standaloneWg.Component, gcOut.Component })
            tilesBefore.AddComponent(comp);
        tilesBefore.AddComponent(regularGroup);
        tilesBefore.AddComponent(prefabGroup);

        var connsBefore = new WaveguideConnectionManager(new WaveguideRouter());
        var regIn = regularGroup.PhysicalPins.First(p => p.Name == "group_in");
        var regOut = regularGroup.PhysicalPins.First(p => p.Name == "group_out");
        var prefIn = prefabGroup.PhysicalPins.First(p => p.Name == "group_in");
        var prefOut = prefabGroup.PhysicalPins.First(p => p.Name == "group_out");
        AddConn(connsBefore, gcSrc.Pins["waveguide"], standaloneWg.Pins["in"]);
        AddConn(connsBefore, standaloneWg.Pins["out"], regIn);
        AddConn(connsBefore, regOut, prefIn);
        AddConn(connsBefore, prefOut, gcOut.Pins["waveguide"]);

        var fieldsBefore = await RunSimulation(tilesBefore, connsBefore, gcSrc, SimWavelength);

        // --- Flatten: replace both groups with individual components + new connections ---
        var tilesAfter = new ComponentListTileManager();
        foreach (var comp in new[] { gcSrc.Component, standaloneWg.Component, gcOut.Component })
            tilesAfter.AddComponent(comp);
        foreach (var child in regularGroup.ChildComponents)
            tilesAfter.AddComponent(child);
        foreach (var child in prefabGroup.ChildComponents)
            tilesAfter.AddComponent(child);

        var connsAfter = new WaveguideConnectionManager(new WaveguideRouter());
        // Reuse same group pin references - they share LogicalPin GUIDs with internal component pins
        AddConn(connsAfter, gcSrc.Pins["waveguide"], standaloneWg.Pins["in"]);
        AddConn(connsAfter, standaloneWg.Pins["out"], regIn);
        AddConn(connsAfter, regOut, prefIn);
        AddConn(connsAfter, prefOut, gcOut.Pins["waveguide"]);
        // Convert frozen internal paths to regular waveguide connections
        foreach (var fp in regularGroup.InternalPaths)
            AddConn(connsAfter, fp.StartPin, fp.EndPin);
        foreach (var fp in prefabGroup.InternalPaths)
            AddConn(connsAfter, fp.StartPin, fp.EndPin);

        var fieldsAfter = await RunSimulation(tilesAfter, connsAfter, gcSrc, SimWavelength);

        // Assert: every pin ID present in both simulations must have identical light values
        var sharedPinIds = fieldsBefore.Keys.Intersect(fieldsAfter.Keys).ToList();
        sharedPinIds.ShouldNotBeEmpty("No shared pin IDs found - simulation setup may be broken");

        foreach (var pinId in sharedPinIds)
        {
            var diff = Complex.Abs(fieldsBefore[pinId] - fieldsAfter[pinId]);
            diff.ShouldBeLessThan(LightMatchTolerance,
                $"Light value changed after flatten at pin {pinId}: " +
                $"before={fieldsBefore[pinId]:F8}, after={fieldsAfter[pinId]:F8}");
        }
    }

    /// <summary>
    /// Verifies that output GC receives non-zero power both before and after flatten.
    /// A group must not swallow light - failing this test means the group's S-Matrix is broken.
    /// </summary>
    [Fact]
    public async Task FlattenGroup_OutputPowerNonZero_BeforeAndAfterFlatten()
    {
        var gcSrc = IntegrationCircuitBuilder.CreateGratingCoupler("GC_src", 0, 0, AllWavelengths);
        var wg1 = CreateWaveguideInfo("WG1", 50, 0);
        var wg2 = CreateWaveguideInfo("WG2", 80, 0);
        var gcOut = IntegrationCircuitBuilder.CreateGratingCoupler("GC_out", 130, 0, AllWavelengths);

        var group = BuildGroup("TestGroup", wg1, wg2, isPrefab: false);
        var groupIn = group.PhysicalPins.First(p => p.Name == "group_in");
        var groupOut = group.PhysicalPins.First(p => p.Name == "group_out");

        // Before flatten: GC_src -> [group: wg1->wg2] -> GC_out
        var tilesBefore = new ComponentListTileManager();
        tilesBefore.AddComponent(gcSrc.Component);
        tilesBefore.AddComponent(group);
        tilesBefore.AddComponent(gcOut.Component);

        var connsBefore = new WaveguideConnectionManager(new WaveguideRouter());
        AddConn(connsBefore, gcSrc.Pins["waveguide"], groupIn);
        AddConn(connsBefore, groupOut, gcOut.Pins["waveguide"]);

        var fieldsBefore = await RunSimulation(tilesBefore, connsBefore, gcSrc, SimWavelength);
        var gcOutLogicalPin = gcOut.LogicalPins[0];
        var powerBefore = Complex.Abs(fieldsBefore.GetValueOrDefault(gcOutLogicalPin.IDInFlow));
        powerBefore.ShouldBeGreaterThan(0,
            "Group swallows all light: GC output has zero power before flatten");

        // After flatten: GC_src -> wg1 -> wg2 -> GC_out (group removed)
        var tilesAfter = new ComponentListTileManager();
        tilesAfter.AddComponent(gcSrc.Component);
        tilesAfter.AddComponent(wg1.Component);
        tilesAfter.AddComponent(wg2.Component);
        tilesAfter.AddComponent(gcOut.Component);

        var connsAfter = new WaveguideConnectionManager(new WaveguideRouter());
        AddConn(connsAfter, gcSrc.Pins["waveguide"], groupIn); // same pin ref, same logical ID
        AddConn(connsAfter, groupOut, gcOut.Pins["waveguide"]);
        foreach (var fp in group.InternalPaths)
            AddConn(connsAfter, fp.StartPin, fp.EndPin);

        var fieldsAfter = await RunSimulation(tilesAfter, connsAfter, gcSrc, SimWavelength);
        var powerAfter = Complex.Abs(fieldsAfter.GetValueOrDefault(gcOutLogicalPin.IDInFlow));
        powerAfter.ShouldBeGreaterThan(0,
            "Flattened circuit swallows all light: GC output has zero power after flatten");
    }

    // ===== Private helpers =====

    /// <summary>
    /// Creates a two-port waveguide component info at the given position.
    /// </summary>
    private static ComponentInfo CreateWaveguideInfo(string name, double x, double y)
    {
        var comp = TestComponentFactory.CreateSimpleTwoPortComponent();
        comp.PhysicalX = x;
        comp.PhysicalY = y;
        comp.HumanReadableName = name;

        var pins = new Dictionary<string, PhysicalPin>
        {
            { "in", comp.PhysicalPins[0] },
            { "out", comp.PhysicalPins[1] }
        };
        var logicalPins = new List<Pin>
        {
            comp.Parts[0, 0].GetPinAt(RectSide.Left),
            comp.Parts[0, 0].GetPinAt(RectSide.Right)
        };
        return new ComponentInfo(comp, pins, logicalPins);
    }

    /// <summary>
    /// Builds a ComponentGroup with two waveguides connected via a frozen internal path.
    /// External pins "group_in" (-> wgA.in) and "group_out" (-> wgB.out) are exposed.
    /// </summary>
    private static ComponentGroup BuildGroup(
        string name, ComponentInfo wgA, ComponentInfo wgB, bool isPrefab)
    {
        var group = new ComponentGroup(name) { IsPrefab = isPrefab };
        group.AddChild(wgA.Component);
        group.AddChild(wgB.Component);

        // Frozen path with empty geometry -> TransmissionCoefficient = Complex.One (no loss)
        var frozenPath = new FrozenWaveguidePath
        {
            StartPin = wgA.Pins["out"],
            EndPin = wgB.Pins["in"],
            Path = new RoutedPath()
        };
        group.AddInternalPath(frozenPath);

        group.AddExternalPin(new GroupPin
        {
            Name = "group_in",
            InternalPin = wgA.Pins["in"],
            RelativeX = 0,
            RelativeY = 0,
            AngleDegrees = 180
        });
        group.AddExternalPin(new GroupPin
        {
            Name = "group_out",
            InternalPin = wgB.Pins["out"],
            RelativeX = 60,
            RelativeY = 0,
            AngleDegrees = 0
        });

        group.ComputeSMatrix();
        return group;
    }

    /// <summary>
    /// Adds a bidirectional waveguide connection between two physical pins.
    /// </summary>
    private static void AddConn(
        WaveguideConnectionManager manager, PhysicalPin start, PhysicalPin end)
    {
        manager.AddExistingConnection(new WaveguideConnection { StartPin = start, EndPin = end });
    }

    /// <summary>
    /// Runs the light simulation and returns the field amplitudes at all pin IDs.
    /// </summary>
    private static async Task<Dictionary<Guid, Complex>> RunSimulation(
        ComponentListTileManager tiles,
        WaveguideConnectionManager conns,
        ComponentInfo gcSource,
        int wavelengthNm)
    {
        var portManager = new PhysicalExternalPortManager();
        var lightSource = new ExternalInput("laser", LaserType.Red, 0, new Complex(1.0, 0));
        portManager.AddLightSource(lightSource, gcSource.LogicalPins[0].IDInFlow);

        var gridManager = GridManager.CreateForSimulation(tiles, conns, portManager);
        var builder = new SystemMatrixBuilder(gridManager);
        var calculator = new GridLightCalculator(builder, gridManager);
        return await calculator.CalculateFieldPropagationAsync(new CancellationTokenSource(), wavelengthNm);
    }
}
