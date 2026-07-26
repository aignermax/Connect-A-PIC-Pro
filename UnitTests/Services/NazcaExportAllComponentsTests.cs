using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;
using Xunit;

namespace UnitTests.Services;

/// <summary>
/// Tests that all component templates (built-in + PDK) can be placed on a canvas
/// and exported to valid Nazca Python code with correct function calls and imports.
/// </summary>
public class NazcaExportAllComponentsTests
{
    [Fact]
    public void Export_AllBuiltInTemplates_EachComponentAppearsInOutput()
    {
        var canvas = new DesignCanvasViewModel();
        // Analysis tools (e.g. ONA Analyzer) are intentionally skipped during
        // GDS / Nazca export — they have no physical counterpart. Filter them
        // out before placing so the per-index assertion below stays in sync.
        // Also skip gdsfactory-backend components (no Nazca function) — they export via the
        // gdsfactory path, not Nazca (issue #570).
        var templates = TestPdkLoader.LoadAllTemplates()
            .Where(t => !string.IsNullOrEmpty(t.NazcaFunctionName) && t.NazcaFunctionName != "__analyzer__")
            .ToList();

        double xOffset = 0;
        foreach (var template in templates)
        {
            var component = ComponentTemplates.CreateFromTemplate(template, xOffset, 0);
            canvas.AddComponent(component, template.Name);
            xOffset += template.WidthMicrometers + 100;
        }

        var exporter = new SimpleNazcaExporter();
        var result = exporter.Export(canvas);

        // Every component should appear as comp_N = ...
        for (int i = 0; i < templates.Count; i++)
        {
            result.ShouldContain($"comp_{i} =",
                customMessage: $"Built-in template '{templates[i].Name}' not found in export");
        }

        // Verify basic structure
        result.ShouldContain("import nazca as nd");
        result.ShouldContain("def create_design():");
        result.ShouldContain("nd.export_gds(filename=gds_filename)"); // Dynamic filename (Issue #172)
    }

    [Fact]
    public void Export_AllBuiltInTemplates_UseDemofabFunctions()
    {
        var canvas = new DesignCanvasViewModel();
        var templates = TestPdkLoader.LoadAllTemplates();

        foreach (var template in templates)
        {
            var component = ComponentTemplates.CreateFromTemplate(template, 0, 0);
            canvas.AddComponent(component, template.Name);
        }

        var exporter = new SimpleNazcaExporter();
        var result = exporter.Export(canvas);

        // Built-in templates should all use demo.* functions (no bare ebeam_* calls)
        result.ShouldContain("demo.");
        result.ShouldNotContain("from siepic_ebeam_pdk import");
    }

    [Fact]
    public void Export_SiepicPdkComponents_IncludesImportAndFunctions()
    {
        var canvas = new DesignCanvasViewModel();
        var loader = new PdkLoader();

        var pdkPath = FindPdkFile("siepic-ebeam-pdk.json");
        if (pdkPath == null)
        {
            // Skip if PDK file not found in test environment
            return;
        }

        var pdk = loader.LoadFromFile(pdkPath);
        pdk.Components.Count.ShouldBeGreaterThan(0, "SiEPIC PDK should have components");

        double xOffset = 0;
        var expectedFunctions = new List<(string FuncName, string? Parameters)>();

        foreach (var pdkComp in pdk.Components)
        {
            var template = ConvertPdkComponentToTemplate(pdkComp, pdk.Name, pdk.NazcaModuleName);
            var component = ComponentTemplates.CreateFromTemplate(template, xOffset, 0);
            canvas.AddComponent(component, template.Name);
            xOffset += template.WidthMicrometers + 100;

            // Track expected function names
            if (!string.IsNullOrEmpty(pdkComp.NazcaFunction))
                expectedFunctions.Add((pdkComp.NazcaFunction, pdkComp.NazcaParameters));
        }

        var exporter = new SimpleNazcaExporter();
        var result = exporter.Export(canvas);

        // Every PDK function should have a stub definition and be called in the design.
        // Function names are sanitized to valid Python identifiers (non-alphanumeric/underscore
        // chars replaced with _); parameterized components get the parameters hash appended
        // (issue #783) — the two "Directional Coupler TE 1550" variants of the shipped PDK
        // must NOT collapse into one stub.
        foreach (var (funcName, parameters) in expectedFunctions)
        {
            var stubName = NazcaStubNaming.StubName(funcName, parameters);
            var pythonFuncName = System.Text.RegularExpressions.Regex.Replace(stubName, @"[^a-zA-Z0-9_.]", "_");
            result.ShouldContain($"def {pythonFuncName}(**kwargs):",
                customMessage: $"Stub definition for '{funcName}' (stub: '{pythonFuncName}') not found in export");
            result.ShouldContain($"{pythonFuncName}(",
                customMessage: $"PDK function call '{funcName}' (stub: '{pythonFuncName}') not found in export");
        }

        // Every component should have a comp_N variable
        for (int i = 0; i < pdk.Components.Count; i++)
        {
            result.ShouldContain($"comp_{i} =",
                customMessage: $"SiEPIC component '{pdk.Components[i].Name}' (comp_{i}) not in export");
        }
    }

    [Fact]
    public void Export_MixedBuiltInAndPdk_BothDemofabAndPdkImportsPresent()
    {
        var canvas = new DesignCanvasViewModel();

        // Add one built-in component
        var builtInTemplates = TestPdkLoader.LoadAllTemplates();
        var splitter = builtInTemplates.First(t => t.Name.Contains("Splitter"));
        var builtInComp = ComponentTemplates.CreateFromTemplate(splitter, 0, 0);
        canvas.AddComponent(builtInComp, splitter.Name);

        // Add one SiEPIC PDK component
        var pdkPath = FindPdkFile("siepic-ebeam-pdk.json");
        if (pdkPath == null) return;

        var loader = new PdkLoader();
        var pdk = loader.LoadFromFile(pdkPath);
        var pdkComp = pdk.Components.First();
        var template = ConvertPdkComponentToTemplate(pdkComp, pdk.Name, pdk.NazcaModuleName);
        var pdkComponent = ComponentTemplates.CreateFromTemplate(template, 500, 0);
        canvas.AddComponent(pdkComponent, template.Name);

        var exporter = new SimpleNazcaExporter();
        var result = exporter.Export(canvas);

        // Demofab import and PDK stub should be present
        result.ShouldContain("import nazca.demofab as demo");
        result.ShouldContain($"def {pdkComp.NazcaFunction}(**kwargs):",
            customMessage: "PDK component stub definition missing");

        // Both components should appear
        result.ShouldContain("comp_0 =");
        result.ShouldContain("comp_1 =");
        result.ShouldContain("demo.");
        result.ShouldContain($"{pdkComp.NazcaFunction}(");
    }

    [Fact]
    public void Export_PdkComponentWithParameters_ParametersIncludedInFunctionCall()
    {
        var canvas = new DesignCanvasViewModel();
        var pdkPath = FindPdkFile("siepic-ebeam-pdk.json");
        if (pdkPath == null) return;

        var loader = new PdkLoader();
        var pdk = loader.LoadFromFile(pdkPath);

        // Find a component with parameters (e.g., dc_halfring_straight has gap, radius)
        var compWithParams = pdk.Components.FirstOrDefault(c =>
            !string.IsNullOrEmpty(c.NazcaParameters));

        if (compWithParams == null) return; // skip if no parameterized components

        var template = ConvertPdkComponentToTemplate(compWithParams, pdk.Name, pdk.NazcaModuleName);
        var component = ComponentTemplates.CreateFromTemplate(template, 0, 0);
        canvas.AddComponent(component, template.Name);

        var exporter = new SimpleNazcaExporter();
        var result = exporter.Export(canvas);

        // Function call should include the parameters — placed on the parameter-specific
        // stub (parameters hash in the name, issue #783).
        var stubName = NazcaStubNaming.StubName(compWithParams.NazcaFunction, compWithParams.NazcaParameters);
        result.ShouldContain($"{stubName}({compWithParams.NazcaParameters})",
            customMessage: $"Parameters missing for {compWithParams.NazcaFunction}");
    }

    /// <summary>
    /// Converts a PDK component draft to a ComponentTemplate (mirrors MainViewModel logic).
    /// </summary>
    private static ComponentTemplate ConvertPdkComponentToTemplate(
        CAP_DataAccess.Components.ComponentDraftMapper.DTOs.PdkComponentDraft pdkComp,
        string pdkName,
        string? nazcaModuleName)
    {
        var pinDefs = pdkComp.Pins.Select(p => new PinDefinition(
            p.Name, p.OffsetXMicrometers, p.OffsetYMicrometers, p.AngleDegrees
        )).ToArray();

        // Calculate Nazca origin offset from first pin position
        // Nazca places components at the first pin's position, so we need to offset
        // from our top-left origin (0,0) to the first pin location
        var firstPin = pdkComp.Pins.FirstOrDefault();
        double nazcaOriginOffsetX = firstPin?.OffsetXMicrometers ?? 0;
        double nazcaOriginOffsetY = firstPin?.OffsetYMicrometers ?? 0;

        return new ComponentTemplate
        {
            Name = pdkComp.Name,
            Category = pdkComp.Category,
            WidthMicrometers = pdkComp.WidthMicrometers,
            HeightMicrometers = pdkComp.HeightMicrometers,
            PinDefinitions = pinDefs,
            NazcaFunctionName = pdkComp.NazcaFunction,
            NazcaParameters = pdkComp.NazcaParameters,
            PdkSource = pdkName,
            NazcaModuleName = nazcaModuleName,
            NazcaOriginOffsetX = nazcaOriginOffsetX,
            NazcaOriginOffsetY = nazcaOriginOffsetY,
            // Minimal S-matrix factory for test (identity pass-through)
            CreateSMatrix = pins =>
            {
                var pinIds = pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();
                return new CAP_Core.LightCalculation.SMatrix(pinIds, new());
            }
        };
    }

    [Fact]
    public void Export_GratingCouplerTE1550_CorrectPositionWithRotation()
    {
        // Issue #66: Verify Grating Coupler TE 1550 position offset is correctly
        // handled. Expected coords are derived from the loaded JSON instead of
        // hardcoded — the JSON is the calibration source of truth.
        var pdkPath = FindPdkFile("siepic-ebeam-pdk.json");
        if (pdkPath == null) return;

        var loader = new PdkLoader();
        var pdk = loader.LoadFromFile(pdkPath);
        var gratingCoupler = pdk.Components.First(c => c.Name == "Grating Coupler TE 1550");
        var template = ConvertPdkComponentToTemplate(gratingCoupler, pdk.Name, pdk.NazcaModuleName);

        var canvas = new DesignCanvasViewModel();
        var component = ComponentTemplates.CreateFromTemplate(template, 100, 100);
        canvas.AddComponent(component, template.Name);

        var exporter = new SimpleNazcaExporter();
        var result = exporter.Export(canvas);

        // Physical (100, 100) + Nazca origin offset, Y-flipped:
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var nx = (100 + template.NazcaOriginOffsetX).ToString("F2", ci);
        var ny = -(100 + template.NazcaOriginOffsetY);
        result.ShouldContain("ebeam_gc_te1550()");
        result.ShouldContain($".put('org', {nx}, {ny.ToString("F2", ci)}, 0)");
    }

    [Fact]
    public void Export_GratingCouplerTE1550_CorrectPositionWithRotation180()
    {
        var pdkPath = FindPdkFile("siepic-ebeam-pdk.json");
        if (pdkPath == null) return;

        var loader = new PdkLoader();
        var pdk = loader.LoadFromFile(pdkPath);
        var gratingCoupler = pdk.Components.First(c => c.Name == "Grating Coupler TE 1550");
        var template = ConvertPdkComponentToTemplate(gratingCoupler, pdk.Name, pdk.NazcaModuleName);

        var canvas = new DesignCanvasViewModel();
        var component = ComponentTemplates.CreateFromTemplate(template, 100, 100);
        component.RotationDegrees = 180;
        canvas.AddComponent(component, template.Name);

        var exporter = new SimpleNazcaExporter();
        var result = exporter.Export(canvas);

        // Rotated placement comes from the bbox re-anchoring formula in
        // NazcaCoordinateMapper (hand-verified per rotation in its unit tests, #565);
        // rotating the origin offset instead would misplace the cell — exactly the
        // misalignment bug #565 covers. Here we pin down that the exporter routes
        // through the mapper and emits the org-anchored put with the negated rotation.
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var placement = NazcaCoordinateMapper.GetCellPlacement(component, null);
        result.ShouldContain("ebeam_gc_te1550()");
        result.ShouldContain(
            $".put('org', {placement.X.ToString("F2", ci)}, {placement.Y.ToString("F2", ci)}, -180)");
    }

    private static string? FindPdkFile(string fileName)
    {
        // Search common locations
        var candidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PDKs", fileName),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "CAP-DataAccess", "PDKs", fileName),
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
