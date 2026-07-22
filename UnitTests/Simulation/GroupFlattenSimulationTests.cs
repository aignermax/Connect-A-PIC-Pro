using System.Numerics;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Connections;
using CAP_Core.ExternalPorts;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.Simulation;

/// <summary>
/// Verifies that light simulation values are identical before and after flattening
/// (ungrouping) ComponentGroups and prefab instances.
///
/// Groups are purely organizational — ungrouping must not affect simulation results.
/// Any discrepancy indicates a "light swallowing" bug in group S-Matrix computation.
/// </summary>
[Trait("Category", "Slow")]
public class GroupFlattenSimulationTests
{
    private const int WavelengthNm = 1550; // matches StandardWaveLengths.RedNM
    private static readonly int[] Wavelengths = { WavelengthNm };
    private const double SimulationTolerance = 1e-10;

    /// <summary>
    /// Creates a complex design with ≥5 element types:
    ///   GratingCouplers, ComponentGroup (with FrozenPath), PrefabInstance,
    ///   inner 2-port components, and WaveguideConnections.
    ///
    /// Runs simulation before and after flattening all groups/prefabs
    /// and asserts all measured amplitudes are identical within tolerance.
    ///
    /// Circuit: GC_in → [GroupA: compA1 --frozen--> compA2] → [PrefabInst: compBCopy] → GC_out
    /// </summary>
    [Fact]
    public async Task LightValues_AreIdentical_BeforeAndAfterFlatteningAllGroups()
    {
        // ---- Shared standalone components ----
        var gcIn = IntegrationCircuitBuilder.CreateGratingCoupler("GC_In", 0, 0, Wavelengths);
        var gcOut = IntegrationCircuitBuilder.CreateGratingCoupler("GC_Out", 200, 0, Wavelengths);

        // ---- Inner components (shared between grouped and flat circuits) ----
        var compA1 = TestComponentFactory.CreateSimpleTwoPortComponent(); // inside GroupA
        var compA2 = TestComponentFactory.CreateSimpleTwoPortComponent(); // inside GroupA
        var compBTemplate = TestComponentFactory.CreateSimpleTwoPortComponent(); // prefab template child

        // ---- Build ComponentGroup A: compA1 --FrozenPath--> compA2 ----
        var groupA = BuildGroupWithFrozenPath(compA1, compA2);

        // ---- Build PrefabInstance (DeepCopy of template) ----
        var prefabTemplate = BuildSingleComponentGroup("PrefabTemplate", compBTemplate);
        prefabTemplate.EnsureSMatrixComputed();
        var prefabInstance = prefabTemplate.DeepCopy();
        prefabInstance.IsPrefab = true;
        prefabInstance.EnsureSMatrixComputed(); // DeepCopy does not copy PhysicalPins — compute to populate them
        var compBCopy = prefabInstance.ChildComponents[0]; // cloned child inside prefab

        // ---- Resolve pins for connections ----
        var gcInPhysPin = gcIn.Pins["waveguide"];
        var gcOutPhysPin = gcOut.Pins["waveguide"];
        var groupInPhysPin = groupA.PhysicalPins.First(p => p.Name == "GroupIn");
        var groupOutPhysPin = groupA.PhysicalPins.First(p => p.Name == "GroupOut");
        var prefabInPhysPin = prefabInstance.PhysicalPins.First(p => p.Name == "PrefabIn");
        var prefabOutPhysPin = prefabInstance.PhysicalPins.First(p => p.Name == "PrefabOut");

        // ---- Port manager (shared: same light injection point in both circuits) ----
        var portManager = new PhysicalExternalPortManager();
        portManager.AddLightSource(
            new ExternalInput("laser", LaserType.Red, 0, new Complex(1.0, 0)),
            gcIn.LogicalPins[0].IDInFlow);

        // ---- GROUPED CIRCUIT ----
        var groupedConn = new WaveguideConnectionManager(new WaveguideRouter());
        groupedConn.AddExistingConnection(MakeConn(gcInPhysPin, groupInPhysPin));
        groupedConn.AddExistingConnection(MakeConn(groupOutPhysPin, prefabInPhysPin));
        groupedConn.AddExistingConnection(MakeConn(prefabOutPhysPin, gcOutPhysPin));

        var groupedTile = new ComponentListTileManager();
        groupedTile.AddComponent(gcIn.Component);
        groupedTile.AddComponent(groupA);
        groupedTile.AddComponent(prefabInstance);
        groupedTile.AddComponent(gcOut.Component);

        var groupedGrid = GridManager.CreateForSimulation(groupedTile, groupedConn, portManager);
        var fieldsGrouped = await RunSimulationAsync(groupedGrid);

        // ---- FLATTENED CIRCUIT ----
        // Same logical pin IDs as grouped circuit:
        // groupA.PhysicalPins["GroupIn"].LogicalPin == compA1.PhysicalPins[0].LogicalPin
        // groupA.PhysicalPins["GroupOut"].LogicalPin == compA2.PhysicalPins[1].LogicalPin
        // prefabInstance.PhysicalPins["PrefabIn"].LogicalPin == compBCopy.PhysicalPins[0].LogicalPin
        var flatConn = new WaveguideConnectionManager(new WaveguideRouter());
        flatConn.AddExistingConnection(MakeConn(gcInPhysPin, compA1.PhysicalPins[0]));
        flatConn.AddExistingConnection(MakeConn(compA1.PhysicalPins[1], compA2.PhysicalPins[0])); // FrozenPath → explicit
        flatConn.AddExistingConnection(MakeConn(compA2.PhysicalPins[1], compBCopy.PhysicalPins[0]));
        flatConn.AddExistingConnection(MakeConn(compBCopy.PhysicalPins[1], gcOutPhysPin));

        var flatTile = new ComponentListTileManager();
        flatTile.AddComponent(gcIn.Component);
        flatTile.AddComponent(compA1);
        flatTile.AddComponent(compA2);
        flatTile.AddComponent(compBCopy);
        flatTile.AddComponent(gcOut.Component);

        var flatGrid = GridManager.CreateForSimulation(flatTile, flatConn, portManager);
        var fieldsFlat = await RunSimulationAsync(flatGrid);

        // ---- ASSERTIONS ----
        // 1. GC output must carry light (circuit is working)
        var outPinInFlow = gcOut.LogicalPins[0].IDInFlow;
        fieldsGrouped.ShouldContainKey(outPinInFlow, "Grouped simulation: GC output pin missing");
        fieldsFlat.ShouldContainKey(outPinInFlow, "Flat simulation: GC output pin missing");

        var groupedOutputPower = fieldsGrouped[outPinInFlow].Magnitude;
        var flatOutputPower = fieldsFlat[outPinInFlow].Magnitude;

        groupedOutputPower.ShouldBeGreaterThan(0,
            "No light reached output GC in grouped simulation — check circuit setup");
        flatOutputPower.ShouldBeGreaterThan(0,
            "No light reached output GC in flat simulation — check circuit setup");

        // 2. Output power must be identical after flattening (primary assertion)
        flatOutputPower.ShouldBe(groupedOutputPower, SimulationTolerance,
            "Output power changed after flattening — light swallowing bug detected");

        // 3. Also compare at the group-to-prefab boundary pin (appears in both circuits)
        var boundaryPinId = compA2.PhysicalPins[1].LogicalPin.IDOutFlow;
        if (fieldsGrouped.ContainsKey(boundaryPinId) && fieldsFlat.ContainsKey(boundaryPinId))
        {
            fieldsFlat[boundaryPinId].Magnitude.ShouldBe(
                fieldsGrouped[boundaryPinId].Magnitude, SimulationTolerance,
                "Boundary amplitude changed after flattening GroupA");
        }

        // 4. Compare at the prefab-to-gcOut boundary pin
        var prefabOutPinId = compBCopy.PhysicalPins[1].LogicalPin.IDOutFlow;
        if (fieldsGrouped.ContainsKey(prefabOutPinId) && fieldsFlat.ContainsKey(prefabOutPinId))
        {
            fieldsFlat[prefabOutPinId].Magnitude.ShouldBe(
                fieldsGrouped[prefabOutPinId].Magnitude, SimulationTolerance,
                "Prefab output amplitude changed after flattening PrefabInstance");
        }
    }

    // ---- Private helpers ----

    /// <summary>
    /// Builds a ComponentGroup containing two 2-port components with a FrozenPath between them.
    /// ExternalPins: "GroupIn" (→ compA.in) and "GroupOut" (→ compB.out).
    /// </summary>
    private static ComponentGroup BuildGroupWithFrozenPath(Component compA, Component compB)
    {
        var group = new ComponentGroup("GroupA");
        group.AddChild(compA);
        group.AddChild(compB);

        // Zero-length frozen path: TransmissionCoefficient = Complex.One
        var frozenPath = new FrozenWaveguidePath
        {
            StartPin = compA.PhysicalPins[1], // "out" pin of compA
            EndPin = compB.PhysicalPins[0],   // "in" pin of compB
            Path = new RoutedPath()            // empty path → zero length → no loss
        };
        group.AddInternalPath(frozenPath);

        group.AddExternalPin(new GroupPin { Name = "GroupIn", InternalPin = compA.PhysicalPins[0] });
        group.AddExternalPin(new GroupPin { Name = "GroupOut", InternalPin = compB.PhysicalPins[1] });
        group.EnsureSMatrixComputed();

        return group;
    }

    /// <summary>
    /// Wraps a single 2-port component in a ComponentGroup as a prefab template.
    /// ExternalPins: "PrefabIn" (→ comp.in) and "PrefabOut" (→ comp.out).
    /// </summary>
    private static ComponentGroup BuildSingleComponentGroup(string name, Component comp)
    {
        var group = new ComponentGroup(name);
        group.AddChild(comp);
        group.AddExternalPin(new GroupPin { Name = "PrefabIn", InternalPin = comp.PhysicalPins[0] });
        group.AddExternalPin(new GroupPin { Name = "PrefabOut", InternalPin = comp.PhysicalPins[1] });
        return group;
    }

    /// <summary>Creates a lossless WaveguideConnection (TransmissionCoefficient = 1).</summary>
    private static WaveguideConnection MakeConn(PhysicalPin start, PhysicalPin end) =>
        new WaveguideConnection { StartPin = start, EndPin = end };

    /// <summary>Runs the light simulation and returns field amplitudes per pin ID.</summary>
    private static async Task<Dictionary<Guid, Complex>> RunSimulationAsync(GridManager grid)
    {
        var builder = new SystemMatrixBuilder(grid);
        var calculator = new GridLightCalculator(builder, grid);
        return await calculator.CalculateFieldPropagationAsync(new CancellationTokenSource(), WavelengthNm);
    }
}
