using CAP.Avalonia.Services.GdsFactoryExport;
using CAP.Avalonia.Services.GdsFactoryExport.MixedBackend;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using Shouldly;
using Xunit;

namespace UnitTests.Export.GdsFactoryExport.MixedBackend;

/// <summary>Tests for the two-script mixed-backend GDS export orchestration (#776).</summary>
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

    private static MixedBackendScriptSet BuildMixedScripts() =>
        new MixedBackendGdsOrchestrator().BuildScripts(
            MixedCanvas(),
            new GdsFactoryExportOptions(GdsFactoryComponentMode.UbcPdkCells),
            metalSpec: null,
            EmptyLibrary,
            Path.Combine(Path.GetTempPath(), "chip1.py"));
}
