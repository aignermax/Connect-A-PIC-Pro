using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Selection;

/// <summary>
/// Tests that <see cref="CAP.Avalonia.Selection.ComponentClipboard"/> preserves and resolves
/// PDK sources (issue #570): pasted copies keep the source component's PDK source, missing
/// sources fall back to <c>PdkSourceResolver</c>, and copied groups expand to their leaf
/// children so process enforcement sees every child's PDK.
/// </summary>
public class ComponentClipboardPdkSourceTests
{
    [Fact]
    public void Paste_ComponentWithTemplatePdkSource_CarriesPdkSourceOntoCopy()
    {
        var canvas = new DesignCanvasViewModel();
        var vm = canvas.AddComponent(TestComponentFactory.CreateStraightWaveGuide(), "T", "SiEPIC PDK");

        canvas.Clipboard.Copy(new[] { vm }, canvas.Connections);
        var result = canvas.Clipboard.Paste(canvas);

        result.ShouldNotBeNull();
        result!.Components[0].TemplatePdkSource.ShouldBe("SiEPIC PDK",
            "dropping the PDK source on paste made copies read as built-in and bypass process enforcement");
    }

    [Fact]
    public void PeekPdkSources_MissingTemplatePdkSource_FallsBackToResolver()
    {
        var canvas = new DesignCanvasViewModel();
        // No template PDK source on the VM — mirrors pasted copies and undo-recreated VMs.
        var vm = canvas.AddComponent(TestComponentFactory.CreateStraightWaveGuide());
        vm.TemplatePdkSource.ShouldBeNull();

        canvas.Clipboard.PdkSourceResolver = _ => "ResolvedPdk";
        canvas.Clipboard.Copy(new[] { vm }, canvas.Connections);

        canvas.Clipboard.PeekPdkSources().ShouldBe(new[] { "ResolvedPdk" });
    }

    [Fact]
    public void PeekPdkSources_CopiedGroup_ExpandsToChildPdkSources()
    {
        var canvas = new DesignCanvasViewModel();
        var group = TestComponentFactory.CreateComponentGroup("Circuit", addChildren: true);
        var firstChild = group.ChildComponents[0];
        var groupVm = canvas.AddComponent(group);

        canvas.Clipboard.PdkSourceResolver = c =>
            ReferenceEquals(c, firstChild) ? "PdkA" : "PdkB";
        canvas.Clipboard.Copy(new[] { groupVm }, canvas.Connections);

        // The group VM itself carries no PDK source — it must expand to its leaf children.
        canvas.Clipboard.PeekPdkSources().ShouldBe(new[] { "PdkA", "PdkB" });
    }
}
