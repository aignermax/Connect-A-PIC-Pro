using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Creation;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using Shouldly;
using System.Numerics;
using Xunit;

namespace UnitTests.Simulation;

/// <summary>
/// Regression tests for parametric prefabs: prefabs of parametric PDK components used
/// to lose their S-matrix and sliders — an instantiated prefab simulated as optically
/// dead because <see cref="GroupTemplateSerializer"/> snapshotted only the numeric
/// matrix (zero for formula-driven components) and dropped all sliders on
/// deserialization.
///
/// These tests build real parametric components from the bundled demo PDK
/// ("Directional Coupler", coupling_ratio slider) and verify the full
/// save → disk → reload → instantiate lifecycle.
/// </summary>
public class PrefabParametricPreservationTests
{
    private const double Tolerance = 1e-9;

    private static Component CreateDirectionalCoupler(double couplingRatio = 50)
    {
        var template = TestPdkLoader.LoadFromPdk("demo-pdk.json")
            .First(t => t.Name == "Directional Coupler");
        var component = ComponentTemplates.CreateFromTemplate(template, 0, 0);
        component.GetSlider(0)!.Value = couplingRatio;
        return component;
    }

    private static Pin LogicalPinOf(Component component, string pinName) =>
        component.PhysicalPins.First(p => p.Name == pinName).LogicalPin!;

    private static ComponentGroup CreateCouplerPrefabGroup(double couplingRatio)
    {
        var coupler = CreateDirectionalCoupler(couplingRatio);
        var group = new ComponentGroup("CouplerPrefab");
        group.AddChild(coupler);
        group.AddExternalPin(new GroupPin
        {
            Name = "In",
            InternalPin = coupler.PhysicalPins.First(p => p.Name == "in1")
        });
        group.AddExternalPin(new GroupPin
        {
            Name = "Out",
            InternalPin = coupler.PhysicalPins.First(p => p.Name == "out2")
        });
        return group;
    }

    [Fact]
    public void FreshParametricComponent_StoresTransfersOnlyAsFormulas()
    {
        // Documents the root cause: a fresh parametric component has zero
        // numeric transfers — the values live exclusively in formula connections.
        var coupler = CreateDirectionalCoupler();

        var matrix = coupler.WaveLengthToSMatrixMap[StandardWaveLengths.RedNM];
        matrix.GetNonNullValues().ShouldBeEmpty(
            "parametric components keep their transfers in formula connections, not the numeric matrix");
        matrix.NonLinearConnections.ShouldNotBeEmpty();
        coupler.GetAllSliders().Count.ShouldBe(1);
    }

    [Fact]
    public void SerializeDeserialize_CapturesEvaluatedNumericTransfers()
    {
        var group = CreateCouplerPrefabGroup(couplingRatio: 70);

        var json = GroupTemplateSerializer.Serialize(group);
        var restored = GroupTemplateSerializer.Deserialize(json);

        restored.ShouldNotBeNull();
        var child = restored!.ChildComponents[0];
        var transfers = child.WaveLengthToSMatrixMap[StandardWaveLengths.RedNM].GetNonNullValues();
        transfers.ShouldNotBeEmpty(
            "the prefab must capture the evaluated parameter values, not the zero matrix");

        var in1 = LogicalPinOf(child, "in1");
        var out2 = LogicalPinOf(child, "out2");
        var cross = transfers[(in1.IDInFlow, out2.IDOutFlow)];
        cross.Magnitude.ShouldBe(Math.Sqrt(0.7), Tolerance,
            "in1→out2 magnitude must follow Sqrt(coupling_ratio / 100) at ratio 70");
        cross.Phase.ShouldBe(Math.PI / 2, 1e-9,
            "in1→out2 carries a 90° phase per the PDK formula");

        var out1 = LogicalPinOf(child, "out1");
        var through = transfers[(in1.IDInFlow, out1.IDOutFlow)];
        through.Magnitude.ShouldBe(Math.Sqrt(0.3), Tolerance,
            "in1→out1 magnitude must follow Sqrt(1 - coupling_ratio / 100) at ratio 70");
    }

    [Fact]
    public void SerializeDeserialize_RestoresSlidersWithSavedValuesAndMetadata()
    {
        var group = CreateCouplerPrefabGroup(couplingRatio: 70);

        var json = GroupTemplateSerializer.Serialize(group);
        var restored = GroupTemplateSerializer.Deserialize(json);

        restored.ShouldNotBeNull();
        var child = restored!.ChildComponents[0];

        var slider = child.GetSlider(0);
        slider.ShouldNotBeNull("sliders must survive the prefab round-trip");
        slider!.Value.ShouldBe(70, Tolerance,
            "the saved coupling ratio must be restored, not reset to the range midpoint");
        slider.MinValue.ShouldBe(0);
        slider.MaxValue.ShouldBe(100);

        child.ParameterDefinitions.Count.ShouldBe(1,
            "parameter metadata must be restored so the properties panel can render editors");
        child.ParameterDefinitions[0].Name.ShouldBe("coupling_ratio");
        child.ParameterDefinitions[0].SliderNumber.ShouldBe(0);
    }

    [Fact]
    public void SerializeDeserialize_KeepsFormulasLive_SliderEditUpdatesTransfers()
    {
        var group = CreateCouplerPrefabGroup(couplingRatio: 70);

        var json = GroupTemplateSerializer.Serialize(group);
        var restored = GroupTemplateSerializer.Deserialize(json);

        var child = restored!.ChildComponents[0];
        var matrix = child.WaveLengthToSMatrixMap[StandardWaveLengths.RedNM];
        matrix.NonLinearConnections.ShouldNotBeEmpty(
            "formula connections must be rebuilt, not flattened into constants");
        matrix.ParametricSnapshot.ShouldNotBeNull();
        matrix.ParametricRebuild.ShouldNotBeNull();

        child.GetSlider(0)!.Value = 30;

        var evaluated = matrix.CreateEvaluatedSnapshot();
        var in1 = LogicalPinOf(child, "in1");
        var out2 = LogicalPinOf(child, "out2");
        var cross = evaluated.GetNonNullValues()[(in1.IDInFlow, out2.IDOutFlow)];
        cross.Magnitude.ShouldBe(Math.Sqrt(0.3), Tolerance,
            "editing the restored slider must drive the rebuilt formula");
    }

    [Fact]
    public void InstantiateTemplate_GivesInstanceLiveSlidersAndIsolatedMatrix()
    {
        var group = CreateCouplerPrefabGroup(couplingRatio: 70);
        group.ComputeSMatrix();

        var tempDir = Path.Combine(Path.GetTempPath(), $"lunima_test_{Guid.NewGuid():N}");
        try
        {
            var library = new GroupLibraryManager(tempDir);
            library.SaveTemplate(group, "CouplerPrefab");

            // Simulate the app restart: reload the library from disk, then instantiate.
            var freshLibrary = new GroupLibraryManager(tempDir);
            freshLibrary.LoadTemplates();
            var template = freshLibrary.Templates.Single(t => t.Name == "CouplerPrefab");

            var instance = freshLibrary.InstantiateTemplate(template, 500, 500);

            var instanceChild = instance.ChildComponents[0];
            instanceChild.GetSlider(0)!.Value.ShouldBe(70, Tolerance,
                "the instance must inherit the parameter values the prefab was saved with");

            var templateChild = template.TemplateGroup!.ChildComponents[0];
            instanceChild.WaveLengthToSMatrixMap[StandardWaveLengths.RedNM]
                .ShouldNotBeSameAs(templateChild.WaveLengthToSMatrixMap[StandardWaveLengths.RedNM],
                    "each instance must own its parametric matrix — sharing binds all instances " +
                    "to the template's slider IDs and leaves instance sliders dead");

            // Edit the instance slider: the instance follows, the template stays.
            instanceChild.GetSlider(0)!.Value = 10;
            var in1 = LogicalPinOf(instanceChild, "in1");
            var out2 = LogicalPinOf(instanceChild, "out2");
            var instanceCross = instanceChild.WaveLengthToSMatrixMap[StandardWaveLengths.RedNM]
                .CreateEvaluatedSnapshot().GetNonNullValues()[(in1.IDInFlow, out2.IDOutFlow)];
            instanceCross.Magnitude.ShouldBe(Math.Sqrt(0.1), Tolerance,
                "the instance slider must drive the instance's own formulas");

            var templateMatrix = templateChild.WaveLengthToSMatrixMap[StandardWaveLengths.RedNM];
            templateMatrix.SliderReference.Values.ShouldAllBe(v => v == 70,
                "editing the instance must not leak into the template's parameter state");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void PrefabInstance_ConductsLight_EndToEnd()
    {
        // The issue's scenario: two parametric couplers chained by a frozen path —
        // the instantiated prefab must not be optically transparent.
        const double ratio = 70;
        var couplerA = CreateDirectionalCoupler(ratio);
        couplerA.PhysicalX = 0;
        couplerA.PhysicalY = 0;
        var couplerB = CreateDirectionalCoupler(ratio);
        couplerB.PhysicalX = 300;
        couplerB.PhysicalY = 0;

        var group = new ComponentGroup("ChainedCouplers");
        group.AddChild(couplerA);
        group.AddChild(couplerB);

        var path = new FrozenWaveguidePath
        {
            StartPin = couplerA.PhysicalPins.First(p => p.Name == "out1"),
            EndPin = couplerB.PhysicalPins.First(p => p.Name == "in1"),
            Path = new RoutedPath(),
            PropagationLossDbPerCm = 0
        };
        path.Path.Segments.Add(new StraightSegment(250, 26, 300, 26, 0));
        group.AddInternalPath(path);

        group.AddExternalPin(new GroupPin
        {
            Name = "In",
            InternalPin = couplerA.PhysicalPins.First(p => p.Name == "in1")
        });
        group.AddExternalPin(new GroupPin
        {
            Name = "Out",
            InternalPin = couplerB.PhysicalPins.First(p => p.Name == "out2")
        });

        var tempDir = Path.Combine(Path.GetTempPath(), $"lunima_test_{Guid.NewGuid():N}");
        try
        {
            var library = new GroupLibraryManager(tempDir);
            library.SaveTemplate(group, "ChainedCouplers");

            var freshLibrary = new GroupLibraryManager(tempDir);
            freshLibrary.LoadTemplates();
            var template = freshLibrary.Templates.Single(t => t.Name == "ChainedCouplers");

            // InstantiateTemplate deep-copies and computes the group S-matrix.
            var instance = freshLibrary.InstantiateTemplate(template, 1000, 1000);

            var groupMatrix = instance.WaveLengthToSMatrixMap[StandardWaveLengths.RedNM];
            var transfers = groupMatrix.GetNonNullValues();
            transfers.ShouldNotBeEmpty(
                "the instantiated prefab must conduct light — a zero matrix is optically dead");

            var inPin = instance.ExternalPins.First(p => p.Name == "In").InternalPin.LogicalPin!;
            var outPin = instance.ExternalPins.First(p => p.Name == "Out").InternalPin.LogicalPin!;
            transfers.ContainsKey((inPin.IDInFlow, outPin.IDOutFlow)).ShouldBeTrue(
                "light injected at the prefab input must reach the prefab output");

            // In → A.out1 (Sqrt(1-r)) → frozen path (lossless) → B.in1 → Out (Sqrt(r), 90°).
            double expectedMagnitude = Math.Sqrt(1 - ratio / 100) * Math.Sqrt(ratio / 100);
            transfers[(inPin.IDInFlow, outPin.IDOutFlow)].Magnitude
                .ShouldBe(expectedMagnitude, 1e-6,
                    "the prefab must transmit with the couplers' combined amplitude");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Deserialize_TemplatePredatingParametricPersistence_LoadsNumericOnly()
    {
        // Old templates have no Sliders/Parametric fields: they must still load with
        // their numeric transfers intact and without sliders.
        var pinInFlow = Guid.NewGuid();
        var pinOutFlow = Guid.NewGuid();
        var json = $$"""
        {
          "GroupName": "Legacy",
          "Description": "",
          "Identifier": "group_legacy",
          "PhysicalX": 0,
          "PhysicalY": 0,
          "WidthMicrometers": 10,
          "HeightMicrometers": 10,
          "Rotation": 0,
          "Children": [
            {
              "IsGroup": false,
              "Identifier": "comp_1",
              "TypeNumber": 0,
              "PhysicalX": 0,
              "PhysicalY": 0,
              "WidthMicrometers": 10,
              "HeightMicrometers": 10,
              "Rotation": 0,
              "Pins": [
                {
                  "Name": "a0",
                  "OffsetX": 0,
                  "OffsetY": 0,
                  "AngleDegrees": 0,
                  "LogicalPinIdInFlow": "{{pinInFlow}}",
                  "LogicalPinIdOutFlow": "{{pinOutFlow}}"
                }
              ],
              "SMatrices": [
                {
                  "WavelengthNm": 1550,
                  "AllPinIds": [ "{{pinInFlow}}", "{{pinOutFlow}}" ],
                  "Transfers": [
                    {
                      "FromPinId": "{{pinInFlow}}",
                      "ToPinId": "{{pinOutFlow}}",
                      "Real": 0.5,
                      "Imaginary": 0.0
                    }
                  ]
                }
              ]
            }
          ],
          "InternalPaths": [],
          "ExternalPins": []
        }
        """;

        var restored = GroupTemplateSerializer.Deserialize(json);

        restored.ShouldNotBeNull();
        var child = restored!.ChildComponents[0];
        child.GetAllSliders().ShouldBeEmpty();
        var transfers = child.WaveLengthToSMatrixMap[StandardWaveLengths.RedNM].GetNonNullValues();
        transfers.Count.ShouldBe(1);
        transfers[(pinInFlow, pinOutFlow)].ShouldBe(new Complex(0.5, 0));
    }
}
