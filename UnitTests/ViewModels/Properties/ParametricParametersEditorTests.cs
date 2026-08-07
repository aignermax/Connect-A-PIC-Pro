using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Properties;
using CAP.Avalonia.ViewModels.Properties.Editors;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels.Properties;

/// <summary>
/// Tests for the parametric parameter editor: components with named
/// physical parameters get labeled, unit-aware rows in the properties panel,
/// and edits flow into the instance's sliders (→ simulation).
/// </summary>
public class ParametricParametersEditorTests
{
    private static ComponentViewModel BuildVm(string templateName)
    {
        var template = TestPdkLoader.LoadFromPdk("demo-pdk.json")
            .First(t => t.Name == templateName);
        var component = ComponentTemplates.CreateFromTemplate(template, 0, 0);
        return new ComponentViewModel(component, template.Name, template.PdkSource);
    }

    [Fact]
    public void Provider_Mmi_ReturnsEditorWithTwoLabeledRows()
    {
        var editor = new ParametricParametersEditorProvider().TryCreateEditor(BuildVm("1x2 MMI Splitter"));

        var vm = editor.ShouldBeOfType<ParametricParametersEditorViewModel>();
        vm.Rows.Count.ShouldBe(2);

        vm.Rows[0].Label.ShouldBe("Insertion Loss");
        vm.Rows[0].Unit.ShouldBe("dB");
        vm.Rows[0].HasUnit.ShouldBeTrue();
        vm.Rows[0].Min.ShouldBe(0);
        vm.Rows[0].Max.ShouldBe(3);
        vm.Rows[0].Value.ShouldBe(0.3, 1e-9);

        vm.Rows[1].Label.ShouldBe("Splitting Ratio (out1)");
        vm.Rows[1].Unit.ShouldBe("%");
        vm.Rows[1].Value.ShouldBe(50, 1e-9);
    }

    [Fact]
    public void Provider_ComponentWithoutParameters_ReturnsNull()
    {
        var straight = TestPdkLoader.LoadFromPdk("demo-pdk.json")
            .First(t => !t.HasSlider && t.ParameterDefinitions.Count == 0);
        var component = ComponentTemplates.CreateFromTemplate(straight, 0, 0);
        var vm = new ComponentViewModel(component, straight.Name, straight.PdkSource);

        new ParametricParametersEditorProvider().TryCreateEditor(vm).ShouldBeNull();
    }

    [Fact]
    public void Factory_WithProductionOrder_Mmi_GetsParametricEditor()
    {
        // Same order as CanvasAndPanelExtensions: parametric before the
        // generic slider editor, so MMI gets named rows, not one anonymous slider.
        var factory = new ComponentEditorFactory(new IComponentEditorProvider[]
        {
            new OnaAnalyzerEditorProvider(),
            new LightSourceEditorProvider(),
            new ParametricParametersEditorProvider(),
            new SliderEditorProvider(),
            new GenericComponentEditorProvider(),
        });

        factory.CreateEditor(BuildVm("1x2 MMI Splitter"))
            .ShouldBeOfType<ParametricParametersEditorViewModel>();
        factory.CreateEditor(BuildVm("Directional Coupler"))
            .ShouldBeOfType<ParametricParametersEditorViewModel>();
    }

    [Fact]
    public void RowValue_Write_UpdatesSliderAndTriggersResimulation()
    {
        var componentVm = BuildVm("1x2 MMI Splitter");
        int resimRequests = 0;
        componentVm.OnSliderChanged = () => resimRequests++;

        var editor = new ParametricParametersEditorViewModel(componentVm);
        editor.Rows[1].Value = 80;

        componentVm.Component.GetSlider(1)!.Value.ShouldBe(80, 1e-9,
            "editing the row must write through to the instance slider");
        resimRequests.ShouldBe(1);
    }

    [Fact]
    public void RowValue_Write_ClampsToParameterRange()
    {
        var componentVm = BuildVm("1x2 MMI Splitter");
        var editor = new ParametricParametersEditorViewModel(componentVm);

        editor.Rows[0].Value = 999;
        editor.Rows[0].Value.ShouldBe(3, 1e-9, "values above max clamp to max");

        editor.Rows[0].Value = -5;
        editor.Rows[0].Value.ShouldBe(0, 1e-9, "values below min clamp to min");
    }

    [Fact]
    public void RowValue_UnchangedWrite_DoesNotRetriggerSimulation()
    {
        var componentVm = BuildVm("Directional Coupler");
        int resimRequests = 0;
        componentVm.OnSliderChanged = () => resimRequests++;

        var editor = new ParametricParametersEditorViewModel(componentVm);
        editor.Rows[0].Value = 50; // already the default

        resimRequests.ShouldBe(0, "no-op writes must not spam the simulation");
    }

    [Fact]
    public void TwoInstances_EditorsAreIndependent()
    {
        var vmA = BuildVm("1x2 MMI Splitter");
        var vmB = BuildVm("1x2 MMI Splitter");

        var editorA = new ParametricParametersEditorViewModel(vmA);
        editorA.Rows[0].Value = 2.5;

        var editorB = new ParametricParametersEditorViewModel(vmB);
        editorB.Rows[0].Value.ShouldBe(0.3, 1e-9,
            "editing instance A must not change instance B (per-instance values)");
    }
}
