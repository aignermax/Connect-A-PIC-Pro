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
    /// Review Finding [0] (placement-livemembers): a value-compatible custom PDK registered
    /// after the active process was saved is missing from the persisted member snapshot, so a
    /// snapshot-only lookup ignored its metal cross-section and exported default metal
    /// (wrong width/layer) — physically wrong GDS. When the caller passes the live
    /// (by-value) member set, it must replace the snapshot as the member filter.
    /// </summary>
    [Fact]
    public void Resolve_EffectiveMemberNames_ReplaceTheSnapshotAsTheMemberFilter()
    {
        var customPdk = new PdkDraft
        {
            Name = "MyCustomFab",
            Process = new ProcessDefinition
            {
                Layers = { new ProcessLayer { Name = "M1", Layer = 41, Datatype = 2 } },
                Xsections =
                {
                    new ProcessXsection { Name = "metal", Kind = XsectionKind.Metal, WidthUm = 6.0, Layers = { "M1" } },
                },
            },
        };
        // Snapshot knows nothing about the custom PDK (it predates it).
        var active = new ActiveProcessSelection("SnapshotFab", null, new[] { "OldFab" }, IsPlayground: false);

        var style = MetalTraceStyleResolver.Resolve(
            active, new[] { customPdk }, effectiveMemberPdkNames: new[] { "MyCustomFab" });

        style.WidthUm.ShouldBe(6.0);
        style.GdsLayer.ShouldBe(41);
        style.GdsDatatype.ShouldBe(2);
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

    /// <summary>
    /// Finding 5 (#733 review): a by-NAME-only draft lookup can pick the wrong file when two
    /// loaded PDKs share a display name (e.g. two custom PDKs authored under the same name from
    /// different files) — an edit meant for one PDK could silently land in the other's JSON.
    /// <see cref="MetalTraceStyleResolver.FindOwnDraft"/> matches by <see cref="PdkDraft.FilePath"/>
    /// first (set by <see cref="PdkLoader"/> at load time), so the correct file always wins even
    /// under a name collision.
    /// </summary>
    [Fact]
    public void FindOwnDraft_PrefersFilePathMatch_OverNameOnlyMatch()
    {
        var drafts = new[]
        {
            new PdkDraft { Name = "Duplicate", FilePath = @"C:\pdks\one.json" },
            new PdkDraft { Name = "Duplicate", FilePath = @"C:\pdks\two.json" },
        };

        var found = MetalTraceStyleResolver.FindOwnDraft(drafts, @"C:\pdks\two.json", "Duplicate");

        found.ShouldBeSameAs(drafts[1]);
    }

    [Fact]
    public void FindOwnDraft_NoFilePath_FallsBackToNameMatch()
    {
        var drafts = new[] { new PdkDraft { Name = "FabA", FilePath = null } };

        var found = MetalTraceStyleResolver.FindOwnDraft(drafts, null, "FabA");

        found.ShouldBeSameAs(drafts[0]);
    }
}
