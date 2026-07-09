using CAP_Core.Components.Process;
using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;

namespace UnitTests.Export.MetalRouting;

/// <summary>
/// Tests for <see cref="MetalTraceStyleResolver"/> — mapping a fabrication process's metal
/// cross-section to the width/layer an electrical trace is drawn with (issue #682).
/// </summary>
public class MetalTraceStyleResolverTests
{
    [Fact]
    public void Resolve_NullProcess_ReturnsDefault()
    {
        MetalTraceStyleResolver.Resolve((ProcessDefinition?)null).ShouldBe(MetalTraceStyle.Default);
    }

    [Fact]
    public void Resolve_ProcessWithoutMetalXsection_ReturnsDefault()
    {
        var process = new ProcessDefinition
        {
            Xsections = { new ProcessXsection { Name = "wg", Kind = XsectionKind.Optical, WidthUm = 0.45 } },
        };

        MetalTraceStyleResolver.Resolve(process).ShouldBe(MetalTraceStyle.Default);
    }

    [Fact]
    public void Resolve_MetalXsectionWithMatchingLayer_UsesProcessWidthAndLayer()
    {
        var process = new ProcessDefinition
        {
            Layers = { new ProcessLayer { Name = "METAL-1", Layer = 41, Datatype = 3 } },
            Xsections =
            {
                new ProcessXsection
                {
                    Name = "MetalDC", Kind = XsectionKind.Metal, WidthUm = 5.0,
                    Layers = { "METAL-1" },
                },
            },
        };

        var style = MetalTraceStyleResolver.Resolve(process);

        style.WidthUm.ShouldBe(5.0);
        style.GdsLayer.ShouldBe(41);
        style.GdsDatatype.ShouldBe(3);
    }

    [Fact]
    public void Resolve_MetalXsectionWithoutResolvableLayer_KeepsWidthButDefaultsLayer()
    {
        // A metal xsection that lists no layer (or a name absent from the stack) still sets the
        // trace width, but the GDS layer falls back to the default so nothing lands on layer 0.
        var process = new ProcessDefinition
        {
            Xsections = { new ProcessXsection { Name = "M", Kind = XsectionKind.Metal, WidthUm = 3.5 } },
        };

        var style = MetalTraceStyleResolver.Resolve(process);

        style.WidthUm.ShouldBe(3.5);
        style.GdsLayer.ShouldBe(MetalTraceStyle.DefaultGdsLayer);
    }

    [Fact]
    public void Resolve_MetalXsectionWithoutLinkedLayer_FallsBackToMetalNamedLayer()
    {
        // A user may add a metal xsection and a "METAL-1" layer without wiring them together;
        // the resolver still finds the metal-named layer for the trace.
        var process = new ProcessDefinition
        {
            Layers =
            {
                new ProcessLayer { Name = "WAVEGUIDE", Layer = 1, Datatype = 0 },
                new ProcessLayer { Name = "METAL-1", Layer = 41, Datatype = 0 },
            },
            Xsections = { new ProcessXsection { Name = "M", Kind = XsectionKind.Metal, WidthUm = 6.0 } },
        };

        var style = MetalTraceStyleResolver.Resolve(process);

        style.GdsLayer.ShouldBe(41);
        style.WidthUm.ShouldBe(6.0);
    }

    [Fact]
    public void Resolve_ActiveProcess_MatchesMemberPdkDraftsByName()
    {
        var draft = new PdkDraft
        {
            Name = "MyFab",
            Process = new ProcessDefinition
            {
                Layers = { new ProcessLayer { Name = "M1", Layer = 12, Datatype = 0 } },
                Xsections =
                {
                    new ProcessXsection { Name = "metal", Kind = XsectionKind.Metal, WidthUm = 4.0, Layers = { "M1" } },
                },
            },
        };
        var active = new ActiveProcessSelection("MyFab", null, new[] { "MyFab" }, IsPlayground: false);

        var style = MetalTraceStyleResolver.Resolve(active, new[] { draft });

        style.WidthUm.ShouldBe(4.0);
        style.GdsLayer.ShouldBe(12);
    }

    [Fact]
    public void Resolve_PlaygroundOrNoSelection_ReturnsDefault()
    {
        MetalTraceStyleResolver.Resolve(null, new List<PdkDraft>()).ShouldBe(MetalTraceStyle.Default);

        var playground = ActiveProcessSelection.Playground();
        MetalTraceStyleResolver.Resolve(playground, new List<PdkDraft>()).ShouldBe(MetalTraceStyle.Default);
    }

    /// <summary>
    /// Finding 7: the by-name PDK/draft lookup was copy-pasted across three call sites
    /// (this resolver, the Fabrication Process dialog, MainWindow's PDK-path wiring) — now a
    /// single shared helper all three call.
    /// </summary>
    [Fact]
    public void FindByName_MatchesCaseInsensitively()
    {
        var drafts = new[]
        {
            new PdkDraft { Name = "FabA" },
            new PdkDraft { Name = "FabB" },
        };

        var found = MetalTraceStyleResolver.FindByName(drafts, "fabb", (PdkDraft d) => d.Name);

        found.ShouldNotBeNull();
        found!.Name.ShouldBe("FabB");
    }

    [Fact]
    public void FindByName_NoMatchOrNullItems_ReturnsNull()
    {
        var drafts = new[] { new PdkDraft { Name = "FabA" } };

        MetalTraceStyleResolver.FindByName(drafts, "Unknown", (PdkDraft d) => d.Name).ShouldBeNull();
        MetalTraceStyleResolver.FindByName((List<PdkDraft>?)null, "FabA", (PdkDraft d) => d.Name).ShouldBeNull();
    }
}
