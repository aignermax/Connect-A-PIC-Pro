using CAP.Avalonia.Services.GdsFactoryExport;
using CAP.Avalonia.Services.GdsFactoryExport.MixedBackend;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Export.GdsFactoryExport.MixedBackend;

/// <summary>Tests for the two-script mixed-backend GDS export orchestration.</summary>
public class MixedBackendGdsOrchestratorTests
{
    private static readonly ComponentTemplate[] EmptyLibrary = Array.Empty<ComponentTemplate>();

    /// <summary>One gdsfactory-native (cspdk) and one nazca-native (SiEPIC) placement.</summary>
    private static DesignCanvasViewModel MixedCanvas()
    {
        var canvas = new DesignCanvasViewModel();
        var gf = TestComponentFactory.CreateBasicComponent();
        gf.Identifier = "GF1";
        gf.NazcaFunctionName = "";
        gf.GdsFactoryFunction = "cspdk.sin300.mmi1x2";
        canvas.AddComponent(gf, "SiN MMI");
        var nazca = TestComponentFactory.CreateBasicComponent();
        nazca.Identifier = "NZ1";
        nazca.NazcaFunctionName = "ebeam_y_1550";
        canvas.AddComponent(nazca, "Y-Branch");
        return canvas;
    }

    [Fact]
    public void IsMixedBackendDesign_BothBackendsPresent_IsTrue()
    {
        MixedBackendGdsOrchestrator.IsMixedBackendDesign(MixedCanvas(), EmptyLibrary)
            .ShouldBeTrue();
    }

    [Fact]
    public void IsMixedBackendDesign_SingleBackend_IsFalse()
    {
        var canvas = new DesignCanvasViewModel();
        var nazca = TestComponentFactory.CreateBasicComponent();
        nazca.NazcaFunctionName = "ebeam_y_1550";
        canvas.AddComponent(nazca, "Y-Branch");

        MixedBackendGdsOrchestrator.IsMixedBackendDesign(canvas, EmptyLibrary)
            .ShouldBeFalse();
    }

    [Fact]
    public void IsMixedBackendDesign_EmptyCanvas_IsFalse()
    {
        MixedBackendGdsOrchestrator.IsMixedBackendDesign(new DesignCanvasViewModel(), EmptyLibrary)
            .ShouldBeFalse();
    }

    [Fact]
    public void PartialScriptPathFor_AppendsSuffixNextToMainScript()
    {
        var main = Path.Combine(Path.GetTempPath(), "chip1.py");

        MixedBackendGdsOrchestrator.PartialScriptPathFor(main)
            .ShouldBe(Path.Combine(Path.GetTempPath(), "chip1_nazca_partial.py"));
    }

    [Fact]
    public void BuildScripts_NazcaPartial_ContainsOnlyNazcaGroupWithDistinctTopCell()
    {
        var scripts = BuildMixedScripts();

        scripts.NazcaPartialScript.ShouldContain(
            MixedBackendGdsOrchestrator.NazcaPartialTopCellName);
        scripts.NazcaPartialScript.ShouldContain("ebeam_y_1550");
        scripts.NazcaPartialScript.ShouldNotContain("cspdk.sin300.mmi1x2");
        // Routed connections are owned by the main gdsfactory script exclusively.
        scripts.NazcaPartialScript.ShouldNotContain("# Waveguide Connections");
    }

    [Fact]
    public void BuildScripts_MainScript_ExcludesNazcaGroupAndMergesPartialGds()
    {
        var scripts = BuildMixedScripts();

        // The cspdk cell is instantiated via the PDK registry under its bare cell name.
        scripts.GdsFactoryScript.ShouldContain("gf.get_component('mmi1x2')");
        scripts.GdsFactoryScript.ShouldNotContain("ebeam_y_1550");
        scripts.GdsFactoryScript.ShouldContain("gf.import_gds");
        scripts.GdsFactoryScript.ShouldContain("chip1_nazca_partial.gds");
        scripts.GdsFactoryScript.ShouldContain("c.add_ref(_nazca_partial)");
    }

    [Fact]
    public void BuildScripts_RawCodeNazcaComponentWithGdsFactoryName_StaysInNazcaPartial()
    {
        // A template can carry BOTH raw code (backend nazca) and a gdsfactory function
        // name (custom-component drafts do). Raw code wins the classification, so the
        // component must land in the nazca partial — not vanish from both scripts.
        var canvas = new DesignCanvasViewModel();
        var gf = TestComponentFactory.CreateBasicComponent();
        gf.Identifier = "GF1";
        gf.NazcaFunctionName = "";
        gf.GdsFactoryFunction = "cspdk.sin300.mmi1x2";
        canvas.AddComponent(gf, "SiN MMI");
        var raw = TestComponentFactory.CreateBasicComponent();
        raw.Identifier = "RC1";
        raw.NazcaFunctionName = "nazca_my_raw";
        raw.GdsFactoryFunction = "mymod.my_raw";
        canvas.AddComponent(raw, "Raw Comp");

        var library = new[]
        {
            new ComponentTemplate
            {
                Name = "My Raw",
                GdsFactoryFunction = "mymod.my_raw",
                RawCode = "def build():\n    pass",
                RawCodeBackend = "nazca",
            },
        };

        var scripts = new MixedBackendGdsOrchestrator().BuildScripts(
            canvas,
            new GdsFactoryExportOptions(GdsFactoryComponentMode.UbcPdkCells),
            metalSpec: null,
            library,
            Path.Combine(Path.GetTempPath(), "chip1.py"));

        scripts.NazcaPartialScript.ShouldContain("RC1");
        scripts.GdsFactoryScript.ShouldNotContain("RC1");
    }

    [Fact]
    public void BuildScripts_BothScriptsCarryDesignerWorkflowHeaders()
    {
        var scripts = BuildMixedScripts();

        scripts.NazcaPartialScript.ShouldContain("part 1 of 2 (nazca)");
        scripts.NazcaPartialScript.ShouldContain("chip1_nazca_partial.gds");
        scripts.NazcaPartialScript.ShouldContain("chip1.py");
        scripts.GdsFactoryScript.ShouldContain("part 2 of 2 (gdsfactory)");
        scripts.GdsFactoryScript.ShouldContain("chip1_nazca_partial.py");
    }

    [Fact]
    public void BuildScripts_ConnectionsStayInMainScript_PartialHasNone()
    {
        // Routed connections belong to the main gdsfactory script exclusively —
        // cross-backend and nazca↔nazca alike; the partial must not emit a single route.
        var canvas = new DesignCanvasViewModel();
        var gf = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        gf.Identifier = "GF1";
        gf.NazcaFunctionName = "";
        gf.GdsFactoryFunction = "cspdk.sin300.mmi1x2";
        canvas.AddComponent(gf, "SiN MMI");
        var nz1 = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        nz1.Identifier = "NZ1";
        nz1.NazcaFunctionName = "ebeam_y_1550";
        canvas.AddComponent(nz1, "Y1");
        var nz2 = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        nz2.Identifier = "NZ2";
        nz2.NazcaFunctionName = "ebeam_y_1550";
        canvas.AddComponent(nz2, "Y2");

        Connect(canvas, gf, nz1);   // cross-backend
        Connect(canvas, nz1, nz2);  // nazca↔nazca

        var scripts = new MixedBackendGdsOrchestrator().BuildScripts(
            canvas,
            new GdsFactoryExportOptions(GdsFactoryComponentMode.UbcPdkCells),
            metalSpec: null,
            EmptyLibrary,
            Path.Combine(Path.GetTempPath(), "chip1.py"));

        scripts.GdsFactoryScript.ShouldContain("gf.components.straight");
        scripts.NazcaPartialScript.ShouldNotContain("# Waveguide Connections");
    }

    private static void Connect(DesignCanvasViewModel canvas, Component from, Component to)
    {
        var startPin = from.PhysicalPins.First(p => p.Name == "out");
        var endPin = to.PhysicalPins.First(p => p.Name == "in");
        var (sx, sy) = startPin.GetAbsolutePosition();
        var (ex, ey) = endPin.GetAbsolutePosition();
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(sx, sy, ex, ey, startPin.GetAbsoluteAngle()));
        canvas.ConnectPinsWithCachedRoute(startPin, endPin, path);
    }

    private static MixedBackendScriptSet BuildMixedScripts() =>
        new MixedBackendGdsOrchestrator().BuildScripts(
            MixedCanvas(),
            new GdsFactoryExportOptions(GdsFactoryComponentMode.UbcPdkCells),
            metalSpec: null,
            EmptyLibrary,
            Path.Combine(Path.GetTempPath(), "chip1.py"));
}
