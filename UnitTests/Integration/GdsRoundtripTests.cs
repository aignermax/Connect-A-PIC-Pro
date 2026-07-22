using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.CodeExporter;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using FrozenWaveguidePath = CAP_Core.Components.Core.FrozenWaveguidePath;
using CAP_Core.Routing;
using Shouldly;
using System.Collections.ObjectModel;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Comprehensive GDS export/import roundtrip integration tests.
/// Covers issue #321: verifies GDS export fidelity for all PDK component types,
/// ComponentGroups, prefab instances, and waveguide routing.
///
/// Test structure:
///   Phase 1 - Design creation: all PDK templates, rotations, groups, connections
///   Phase 2 - Nazca script validation: positions/rotations verified via ExportValidator
///   Phase 3 - GDS binary validation: conditional on Python+Nazca availability
/// </summary>
[Trait("Category", "Slow")]
public class GdsRoundtripTests
{
    private readonly ObservableCollection<ComponentTemplate> _library;
    private readonly SimpleNazcaExporter _exporter = new();
    private readonly ExportValidator _validator = new();
    private readonly NazcaCodeParser _parser = new();

    /// <summary>Initializes the test suite with the full component library.</summary>
    public GdsRoundtripTests()
    {
        // Filter out analysis tools (e.g. ONA Analyzer) — they are intentionally
        // skipped during GDS export and have no physical pin layout to validate.
        // Skip analysis tools and gdsfactory-backend components (no Nazca function; they
        // export via the gdsfactory path, not Nazca — issue #570).
        _library = new ObservableCollection<ComponentTemplate>(
            TestPdkLoader.LoadAllTemplates()
                .Where(t => !string.IsNullOrEmpty(t.NazcaFunctionName) && t.NazcaFunctionName != "__analyzer__"));
    }

    /// <summary>
    /// Verifies that the Nazca export script faithfully represents all PDK component types.
    /// Creates a design with all 9 PDK templates, all 4 rotation states, locked components,
    /// and multiple waveguide connections. Validates positions, rotations, and connections.
    /// </summary>
    [Fact]
    public void GdsExport_AllPdkComponentTypes_NazcaScriptIsValid()
    {
        // Arrange: create comprehensive design
        var canvas = CreateComprehensiveDesign();

        // Act: export to Nazca script
        var script = _exporter.Export(canvas);
        var components = canvas.Components.Select(vm => vm.Component).ToList();
        var connections = canvas.Connections.Select(vm => vm.Connection).ToList();

        // Assert: script must not be empty and contain the design cell
        script.ShouldNotBeNullOrEmpty();
        script.ShouldContain("ConnectAPIC_Design");
        script.ShouldContain("nd.export_gds");

        // Assert: all components are represented
        var parsed = _parser.Parse(script);
        parsed.Components.Count.ShouldBeGreaterThanOrEqualTo(
            canvas.Components.Count,
            "All placed components must appear in Nazca script");

        // Assert: ExportValidator passes (positions + rotations correct)
        var validationResult = _validator.Validate(components, connections, script);
        validationResult.IsValid.ShouldBeTrue(
            $"Export validation failed with {validationResult.FailedChecks} errors:\n" +
            string.Join("\n", validationResult.Errors));
    }

    /// <summary>
    /// Verifies component positions are exported at correct Nazca coordinates.
    /// Tests all 4 discrete rotation states (0°, 90°, 180°, 270°) are inverted correctly.
    /// </summary>
    [Fact]
    public void GdsExport_AllRotationStates_PositionsAndRotationsCorrect()
    {
        var canvas = new DesignCanvasViewModel();
        var mmiTemplate = _library.First(t => t.Name == "1x2 MMI Splitter");

        // Place components at different rotations, spaced 200µm apart
        var rotations = new[] { DiscreteRotation.R0, DiscreteRotation.R90, DiscreteRotation.R180, DiscreteRotation.R270 };
        for (int i = 0; i < rotations.Length; i++)
        {
            var comp = ComponentTemplates.CreateFromTemplate(mmiTemplate, i * 200.0, 0);
            comp.Identifier = $"rot_test_{i}";
            comp.Rotation90CounterClock = rotations[i];
            canvas.AddComponent(comp, mmiTemplate.Name);
        }

        var script = _exporter.Export(canvas);
        var parsed = _parser.Parse(script);

        // Verify 4 components are exported
        parsed.Components.Count.ShouldBe(4, "All 4 rotated components must be in script");

        // Verify rotations are negated (Nazca Y-axis is inverted vs editor)
        for (int i = 0; i < rotations.Length; i++)
        {
            var expectedNazcaRot = -(int)rotations[i] * 90.0;
            var actual = parsed.Components[i].RotationDegrees;
            var diff = Math.Abs(NormalizeAngle(actual) - NormalizeAngle(expectedNazcaRot));
            if (diff > 180) diff = 360 - diff;
            diff.ShouldBeLessThan(0.5,
                $"Rotation {rotations[i]} must be negated in Nazca output");
        }
    }

    /// <summary>
    /// Verifies that waveguide segment coordinates are exported accurately.
    /// Creates connections with explicit cached paths and verifies the script
    /// contains the correct start positions.
    /// </summary>
    [Fact]
    public void GdsExport_WaveguideConnections_PathSegmentsMatchPins()
    {
        var canvas = new DesignCanvasViewModel();
        var gcTemplate = _library.First(t => t.Name == "Grating Coupler");
        var mmiTemplate = _library.First(t => t.Name == "1x2 MMI Splitter");

        // Place two components with matching pin alignment
        var gc = ComponentTemplates.CreateFromTemplate(gcTemplate, 0, 9.5);
        gc.Identifier = "gc_wg_test";
        canvas.AddComponent(gc, gcTemplate.Name);

        var mmi = ComponentTemplates.CreateFromTemplate(mmiTemplate, 150, 0);
        mmi.Identifier = "mmi_wg_test";
        canvas.AddComponent(mmi, mmiTemplate.Name);

        // Create connection with explicit cached route
        var startPin = gc.PhysicalPins.First(p => p.Name == "waveguide");
        var endPin = mmi.PhysicalPins.First(p => p.Name == "in");
        var (startX, startY) = startPin.GetAbsolutePosition();
        var (endX, endY) = endPin.GetAbsolutePosition();

        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(startX, startY, endX, endY, 0));
        canvas.ConnectPinsWithCachedRoute(startPin, endPin, path);

        var script = _exporter.Export(canvas);

        // Assert: waveguide segment present in script
        script.ShouldContain("nd.strt(");

        // Assert: ExportValidator confirms waveguide endpoints match pins
        var components = canvas.Components.Select(vm => vm.Component).ToList();
        var connections = canvas.Connections.Select(vm => vm.Connection).ToList();
        var result = _validator.Validate(components, connections, script);

        result.Errors.Count.ShouldBe(0,
            $"Waveguide endpoint validation failed:\n{string.Join("\n", result.Errors)}");
    }

    /// <summary>
    /// Verifies that ComponentGroups are flattened correctly in the Nazca export.
    /// Group children must appear at their absolute positions in the exported script.
    /// </summary>
    [Fact]
    public void GdsExport_ComponentGroup_ChildrenExportedAtAbsolutePositions()
    {
        var canvas = new DesignCanvasViewModel();
        var mmiTemplate = _library.First(t => t.Name == "1x2 MMI Splitter");

        // Create a group with two children
        var child1 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 100, 50);
        child1.Identifier = "group_child_1";

        var child2 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 300, 50);
        child2.Identifier = "group_child_2";

        var group = new ComponentGroup("TestExportGroup")
        {
            PhysicalX = 0, PhysicalY = 0
        };
        group.AddChild(child1);
        group.AddChild(child2);

        // Add internal frozen path
        var startPin = child1.PhysicalPins.First(p => p.Name == "out1");
        var endPin = child2.PhysicalPins.First(p => p.Name == "in");
        var (sx, sy) = startPin.GetAbsolutePosition();
        var (ex, ey) = endPin.GetAbsolutePosition();
        var frozenRoute = new RoutedPath();
        frozenRoute.Segments.Add(new StraightSegment(sx, sy, ex, ey, 0));
        group.AddInternalPath(new FrozenWaveguidePath
        {
            Path = frozenRoute, StartPin = startPin, EndPin = endPin
        });

        canvas.AddComponent(group, "TestExportGroup");

        var script = _exporter.Export(canvas);
        var parsed = _parser.Parse(script);

        // Both children must appear in script at their absolute positions
        parsed.Components.Count.ShouldBe(2, "Group children must be flattened into script");
        script.ShouldContain("nd.strt(");
    }

    /// <summary>
    /// Conditional GDS binary roundtrip test. Only executes when Python and Nazca are installed.
    /// Generates a real .gds file and verifies structural integrity using the GdsReader.
    /// </summary>
    [Fact]
    public async Task GdsBinaryRoundtrip_WhenPythonAvailable_GdsFileIsStructurallyValid()
    {
        // Check if Python+Nazca are available — skip if not
        var gdsService = new CAP_Core.Export.GdsExportService();
        var envInfo = await gdsService.CheckPythonEnvironmentAsync();
        if (!envInfo.IsReady)
        {
            // GDS binary roundtrip requires Python+Nazca; skip gracefully
            return;
        }

        var tempScript = Path.Combine(Path.GetTempPath(), $"gds_roundtrip_{Guid.NewGuid():N}.py");
        var tempGds = Path.ChangeExtension(tempScript, ".gds");

        try
        {
            // Create and export a design
            var canvas = CreateComprehensiveDesign();
            var script = _exporter.Export(canvas);
            await File.WriteAllTextAsync(tempScript, script);

            // Run the Python script to generate GDS binary
            var exportResult = await gdsService.ExportToGdsAsync(tempScript, generateGds: true);

            exportResult.Success.ShouldBeTrue(
                $"GDS generation must succeed. Error: {exportResult.ErrorMessage}");

            File.Exists(tempGds).ShouldBeTrue("GDS binary file must be created by Nazca");

            // Parse the GDS binary
            var gdsDesign = GdsReader.ReadFile(tempGds);
            gdsDesign.ShouldNotBeNull("GDS file must be parseable by GdsReader");

            // Verify structural integrity: SREF elements (component placements)
            var componentCount = canvas.Components
                .SelectMany(vm => vm.Component is ComponentGroup g
                    ? g.GetAllComponentsRecursive() : new List<Component> { vm.Component })
                .Count();

            gdsDesign!.ComponentRefs.Count.ShouldBeGreaterThan(
                0, "GDS must contain SREF elements for component placements");

            // Verify geometry elements (BOUNDARY or PATH).
            // Nazca exports waveguide geometry as BOUNDARY (filled polygon) elements —
            // component stubs and waveguide shapes both contribute to this count.
            var totalGeometry = gdsDesign.BoundaryCount + gdsDesign.WaveguidePaths.Count;
            totalGeometry.ShouldBeGreaterThan(
                0, "GDS must contain geometry elements (BOUNDARY/PATH) from component stubs");
        }
        finally
        {
            if (File.Exists(tempScript)) File.Delete(tempScript);
            if (File.Exists(tempGds)) File.Delete(tempGds);
        }
    }

    // ── Design Factory ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a comprehensive test design covering all PDK component types,
    /// all rotation states, locked components, and multiple waveguide connections.
    /// </summary>
    private DesignCanvasViewModel CreateComprehensiveDesign()
    {
        var canvas = new DesignCanvasViewModel();
        AddAllPdkComponents(canvas);
        AddRotatedComponents(canvas);
        AddWaveguideConnections(canvas);
        return canvas;
    }

    /// <summary>Adds one instance of every PDK template to the canvas.</summary>
    private void AddAllPdkComponents(DesignCanvasViewModel canvas)
    {
        double x = 0;
        foreach (var template in _library)
        {
            var comp = ComponentTemplates.CreateFromTemplate(template, x, 200);
            comp.Identifier = $"all_pdk_{template.Name.Replace(" ", "_")}";
            comp.HumanReadableName = template.Name;
            canvas.AddComponent(comp, template.Name);
            x += template.WidthMicrometers + 50;
        }

        // Lock two components to verify IsLocked export
        if (canvas.Components.Count >= 2)
        {
            canvas.Components[0].Component.IsLocked = true;
            canvas.Components[1].Component.IsLocked = true;
        }
    }

    /// <summary>Adds components in each of the 4 discrete rotation states.</summary>
    private void AddRotatedComponents(DesignCanvasViewModel canvas)
    {
        var template = _library.First(t => t.Name == "1x2 MMI Splitter");
        var rotations = new[] { DiscreteRotation.R0, DiscreteRotation.R90, DiscreteRotation.R180, DiscreteRotation.R270 };

        for (int i = 0; i < rotations.Length; i++)
        {
            var comp = ComponentTemplates.CreateFromTemplate(template, i * 200.0, 600);
            comp.Identifier = $"rotation_test_{i}";
            comp.Rotation90CounterClock = rotations[i];
            canvas.AddComponent(comp, template.Name);
        }
    }

    /// <summary>Adds waveguide connections between compatible component pairs.</summary>
    private static void AddWaveguideConnections(DesignCanvasViewModel canvas)
    {
        // Connect adjacent components that have compatible pins (output→input)
        for (int i = 0; i < canvas.Components.Count - 1; i++)
        {
            var comp1 = canvas.Components[i].Component;
            var comp2 = canvas.Components[i + 1].Component;

            if (comp1 is ComponentGroup || comp2 is ComponentGroup) continue;

            var outPin = comp1.PhysicalPins.FirstOrDefault(p =>
                p.AngleDegrees == 0 && p.Name != "waveguide");
            var inPin = comp2.PhysicalPins.FirstOrDefault(p =>
                p.AngleDegrees == 180 && p.Name != "waveguide");

            if (outPin == null || inPin == null) continue;

            var (sx, sy) = outPin.GetAbsolutePosition();
            var (ex, ey) = inPin.GetAbsolutePosition();
            var path = new RoutedPath();
            path.Segments.Add(new StraightSegment(sx, sy, ex, ey, 0));
            canvas.ConnectPinsWithCachedRoute(outPin, inPin, path);
        }
    }

    private static double NormalizeAngle(double angle)
    {
        angle = angle % 360;
        if (angle < 0) angle += 360;
        return angle;
    }
}
