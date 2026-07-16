using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.ViewModels.Library;

/// <summary>
/// Verifies <see cref="CanvasComponentTemplateResolver"/>, which routes the canvas "Edit
/// Component…" context-menu entry (design 2026-07-16-pdk-ux-polish T4) to the correct PDK
/// template — the same PdkSource-then-Name matching used elsewhere
/// (<c>TemplatePdkSource ?? ResolveComponentPdkSource</c>) plus a <c>TemplateName</c> match.
/// </summary>
public class CanvasComponentTemplateResolverTests
{
    private static ComponentTemplate MakeTemplate(string name, string pdkSource) =>
        new() { Name = name, PdkSource = pdkSource };

    [Fact]
    public void Resolve_MatchesByTemplatePdkSourceAndTemplateName()
    {
        var compVm = TestComponentFactory.CreateComponentViewModel();
        compVm.TemplatePdkSource = "Demo PDK";
        compVm.TemplateName = "Straight Waveguide";
        var library = new[]
        {
            MakeTemplate("Straight Waveguide", "Demo PDK"),
            MakeTemplate("Straight Waveguide", "Other PDK"), // same name, different PDK — must not match
        };

        var result = CanvasComponentTemplateResolver.Resolve(compVm, library, resolvePdkSource: null);

        result.ShouldBe(library[0]);
    }

    [Fact]
    public void Resolve_FallsBackToResolvePdkSource_WhenTemplatePdkSourceIsNull()
    {
        var compVm = TestComponentFactory.CreateComponentViewModel();
        compVm.TemplatePdkSource = null;
        compVm.TemplateName = "Straight Waveguide";
        var library = new[] { MakeTemplate("Straight Waveguide", "Demo PDK") };

        var result = CanvasComponentTemplateResolver.Resolve(
            compVm, library, resolvePdkSource: _ => "Demo PDK");

        result.ShouldBe(library[0]);
    }

    [Fact]
    public void Resolve_PrefersTemplatePdkSource_OverFallback()
    {
        var compVm = TestComponentFactory.CreateComponentViewModel();
        compVm.TemplatePdkSource = "Demo PDK";
        compVm.TemplateName = "Straight Waveguide";
        var library = new[]
        {
            MakeTemplate("Straight Waveguide", "Demo PDK"),
            MakeTemplate("Straight Waveguide", "Wrong PDK"),
        };
        var fallbackCalled = false;

        var result = CanvasComponentTemplateResolver.Resolve(
            compVm, library, resolvePdkSource: _ => { fallbackCalled = true; return "Wrong PDK"; });

        result.ShouldBe(library[0]);
        fallbackCalled.ShouldBeFalse();
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenNoTemplateMatches_PdkDeletedCase()
    {
        var compVm = TestComponentFactory.CreateComponentViewModel();
        compVm.TemplatePdkSource = "Deleted PDK";
        compVm.TemplateName = "Straight Waveguide";
        var library = new[] { MakeTemplate("Straight Waveguide", "Demo PDK") };

        var result = CanvasComponentTemplateResolver.Resolve(compVm, library, resolvePdkSource: null);

        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenTemplateNameIsNull()
    {
        var compVm = TestComponentFactory.CreateComponentViewModel();
        compVm.TemplatePdkSource = "Demo PDK";
        compVm.TemplateName = null;
        var library = new[] { MakeTemplate("Straight Waveguide", "Demo PDK") };

        var result = CanvasComponentTemplateResolver.Resolve(compVm, library, resolvePdkSource: null);

        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenPdkSourceCannotBeResolvedAtAll()
    {
        var compVm = TestComponentFactory.CreateComponentViewModel();
        compVm.TemplatePdkSource = null;
        compVm.TemplateName = "Straight Waveguide";
        var library = new[] { MakeTemplate("Straight Waveguide", "Demo PDK") };

        var result = CanvasComponentTemplateResolver.Resolve(
            compVm, library, resolvePdkSource: (Component _) => null);

        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_MatchesPdkSourceAndName_CaseInsensitively_LikeTheNeighborMatchers()
    {
        var compVm = TestComponentFactory.CreateComponentViewModel();
        compVm.TemplatePdkSource = "demo pdk";
        compVm.TemplateName = "STRAIGHT WAVEGUIDE";
        var library = new[] { MakeTemplate("Straight Waveguide", "Demo PDK") };

        var result = CanvasComponentTemplateResolver.Resolve(compVm, library, resolvePdkSource: null);

        result.ShouldBe(library[0]);
    }

    [Fact]
    public void ResolveEditable_ReturnsTemplate_WhenResolvableAndEditable()
    {
        var compVm = TestComponentFactory.CreateComponentViewModel();
        compVm.TemplatePdkSource = "Demo PDK";
        compVm.TemplateName = "Straight Waveguide";
        var library = new[] { MakeTemplate("Straight Waveguide", "Demo PDK") };

        var result = CanvasComponentTemplateResolver.ResolveEditable(
            compVm, library, resolvePdkSource: null, canEditTemplate: _ => true);

        result.ShouldBe(library[0]);
    }

    [Fact]
    public void ResolveEditable_ReturnsNull_WhenTemplateResolvesButIsNotEditable()
    {
        var compVm = TestComponentFactory.CreateComponentViewModel();
        compVm.TemplatePdkSource = "Demo PDK";
        compVm.TemplateName = "Straight Waveguide";
        var library = new[] { MakeTemplate("Straight Waveguide", "Demo PDK") };

        var result = CanvasComponentTemplateResolver.ResolveEditable(
            compVm, library, resolvePdkSource: null, canEditTemplate: _ => false);

        result.ShouldBeNull();
    }

    [Fact]
    public void ResolveEditable_ReturnsNull_ForGroupLikeVmWithoutTemplate_WithoutAskingCanEdit()
    {
        // ComponentGroups (and legacy instances) carry no TemplateName — the caller must fall
        // back to the classic per-instance settings dialog, not report a resolver error.
        var compVm = TestComponentFactory.CreateComponentViewModel();
        compVm.TemplatePdkSource = "Demo PDK";
        compVm.TemplateName = null;
        var library = new[] { MakeTemplate("Straight Waveguide", "Demo PDK") };
        var canEditCalled = false;

        var result = CanvasComponentTemplateResolver.ResolveEditable(
            compVm, library, resolvePdkSource: null,
            canEditTemplate: _ => { canEditCalled = true; return true; });

        result.ShouldBeNull();
        canEditCalled.ShouldBeFalse();
    }
}
