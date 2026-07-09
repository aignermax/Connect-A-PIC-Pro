using CAP.Avalonia.Services.GdsFactoryExport;
using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;

namespace UnitTests.Export.GdsFactoryExport;

/// <summary>
/// Tests the pre-Nazca-export detection of gdsfactory-native components — the components a
/// Nazca script cannot express and that must not be dropped silently (#570 field test).
/// </summary>
public class NazcaExportGuardTests
{
    private static CAP_Core.Components.Core.Component MakeComponent(
        string id, string? gdsFactoryFunction, string nazcaFunction = "")
    {
        var c = TestComponentFactory.CreateBasicComponent();
        c.Identifier = id;
        c.NazcaFunctionName = nazcaFunction;
        c.GdsFactoryFunction = gdsFactoryFunction;
        return c;
    }

    [Fact]
    public void CollectGdsFactoryNativeComponents_ReturnsOnlyModuleQualifiedGdsFactoryComponents()
    {
        var canvas = new DesignCanvasViewModel();
        canvas.AddComponent(MakeComponent("SIN1", "cspdk.sin300.mmi1x2"), "SiN MMI");
        canvas.AddComponent(MakeComponent("NZ1", null, "ebeam_y_1550"), "Y");
        canvas.AddComponent(MakeComponent("BARE", "straight"), "bare");   // dotless: not gdsfactory-native

        var found = NazcaExportGuard.CollectGdsFactoryNativeComponents(canvas);

        found.Select(c => c.Identifier).ShouldBe(new[] { "SIN1" });
    }

    [Fact]
    public void CollectGdsFactoryNativeComponents_PureNazcaDesign_ReturnsEmpty()
    {
        var canvas = new DesignCanvasViewModel();
        canvas.AddComponent(MakeComponent("NZ1", null, "ebeam_y_1550"), "Y");

        NazcaExportGuard.CollectGdsFactoryNativeComponents(canvas).ShouldBeEmpty();
    }

    [Fact]
    public void CollectGdsFactoryNativeComponents_FindsGroupChildren()
    {
        var canvas = new DesignCanvasViewModel();
        var group = new CAP_Core.Components.Core.ComponentGroup("Grp");
        group.AddChild(MakeComponent("SINCHILD", "cspdk.sin300.straight"));
        canvas.AddComponent(group, "Group");

        var found = NazcaExportGuard.CollectGdsFactoryNativeComponents(canvas);

        found.Select(c => c.Identifier).ShouldContain("SINCHILD");
    }
}
