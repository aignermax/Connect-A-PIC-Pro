using CAP_Core.Components.Process;
using CAP_Core.Routing.InterconnectRouting;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.InterconnectRouting;

/// <summary>
/// Tests for <see cref="WaveguideBendRadiusResolver"/> — deriving the minimum allowed
/// waveguide bend radius (µm) from the active fabrication process (issue #574).
/// </summary>
public class WaveguideBendRadiusResolverTests
{
    [Fact]
    public void Resolve_Definition_ReturnsSmallestOpticalMinimum()
    {
        var definition = new ProcessDefinition
        {
            Name = "Demo",
            Xsections = new List<ProcessXsection>
            {
                new() { Name = "E200", Kind = XsectionKind.Optical, MinRadiusUm = 50 },
                new() { Name = "E600", Kind = XsectionKind.Optical, MinRadiusUm = 30 },
                new() { Name = "MetalDC", Kind = XsectionKind.Metal, MinRadiusUm = 5 }
            }
        };

        WaveguideBendRadiusResolver.Resolve(new[] { definition }).ShouldBe(30);
    }

    [Fact]
    public void Resolve_NoOpticalMinimum_FallsBackToAbsoluteFloor()
    {
        var definition = new ProcessDefinition
        {
            Name = "NoMin",
            Xsections = new List<ProcessXsection>
            {
                new() { Name = "E200", Kind = XsectionKind.Optical, MinRadiusUm = 0 },
                new() { Name = "MetalDC", Kind = XsectionKind.Metal, MinRadiusUm = 8 }
            }
        };

        WaveguideBendRadiusResolver.Resolve(new[] { definition })
            .ShouldBe(WaveguideBendRadiusResolver.FallbackMinimumMicrometers);
    }

    [Fact]
    public void Resolve_NullProcesses_FallsBackToAbsoluteFloor()
    {
        WaveguideBendRadiusResolver.Resolve((IEnumerable<ProcessDefinition?>?)null)
            .ShouldBe(WaveguideBendRadiusResolver.FallbackMinimumMicrometers);
    }

    [Fact]
    public void Resolve_ActiveProcess_ResolvesMemberPdkByNameCaseInsensitive()
    {
        var pdk = new PdkDraft
        {
            Name = "Demo PDK",
            Process = new ProcessDefinition
            {
                Name = "Demo",
                Xsections = new List<ProcessXsection>
                {
                    new() { Name = "E1700", Kind = XsectionKind.Optical, MinRadiusUm = 100 }
                }
            }
        };
        var active = new ActiveProcessSelection(
            "Demo", Fingerprint: null, MemberPdkNames: new List<string> { "demo pdk" }, IsPlayground: false);

        WaveguideBendRadiusResolver.Resolve(active, new List<PdkDraft> { pdk }).ShouldBe(100);
    }

    [Fact]
    public void Resolve_Playground_FallsBackToAbsoluteFloor()
    {
        var pdk = new PdkDraft
        {
            Name = "Demo PDK",
            Process = new ProcessDefinition
            {
                Xsections = new List<ProcessXsection>
                {
                    new() { Name = "E1700", Kind = XsectionKind.Optical, MinRadiusUm = 100 }
                }
            }
        };

        WaveguideBendRadiusResolver.Resolve(ActiveProcessSelection.Playground(), new List<PdkDraft> { pdk })
            .ShouldBe(WaveguideBendRadiusResolver.FallbackMinimumMicrometers);
    }

    [Fact]
    public void Resolve_NullSelectionOrDrafts_FallsBackToAbsoluteFloor()
    {
        WaveguideBendRadiusResolver.Resolve(null, new List<PdkDraft>())
            .ShouldBe(WaveguideBendRadiusResolver.FallbackMinimumMicrometers);
    }

    [Fact]
    public void Resolve_EffectiveMemberNames_ReplaceTheSnapshotAsTheFilter()
    {
        var customPdk = new PdkDraft
        {
            Name = "MyCustomFab",
            Process = new ProcessDefinition
            {
                Name = "MyCustomFab",
                Xsections = new List<ProcessXsection>
                {
                    new() { Name = "Strip", Kind = XsectionKind.Optical, MinRadiusUm = 25 }
                }
            }
        };
        // Snapshot predates the custom PDK and names a member that is not loaded.
        var active = new ActiveProcessSelection(
            "SnapshotFab", Fingerprint: null, MemberPdkNames: new List<string> { "OldFab" }, IsPlayground: false);

        WaveguideBendRadiusResolver.Resolve(
            active, new List<PdkDraft> { customPdk }, effectiveMemberPdkNames: new[] { "MyCustomFab" })
            .ShouldBe(25);
    }

    [Fact]
    public void ResolveForEndpointPdkNames_TwoChipletProcesses_StricterEndpointGoverns()
    {
        // The #937 scenario: a Cornerstone SiN chiplet (30 µm foundry floor) next to a
        // SiEPIC SOI chiplet (5 µm). A cross-chiplet connection must keep the stricter
        // floor — the canvas-wide minimum-over-members (5 µm) under-enforced Cornerstone.
        var drafts = new List<PdkDraft> { CornerstoneSinPdk(), SiepicSoiPdk() };

        WaveguideBendRadiusResolver.ResolveForEndpointPdkNames("Cornerstone SiN", "SiEPIC SOI", drafts)
            .ShouldBe(30);
        WaveguideBendRadiusResolver.ResolveForEndpointPdkNames("SiEPIC SOI", "Cornerstone SiN", drafts)
            .ShouldBe(30);
    }

    [Fact]
    public void ResolveForEndpointPdkNames_SameChipletEndpoints_UsesThatChipletsFloor()
    {
        var drafts = new List<PdkDraft> { CornerstoneSinPdk(), SiepicSoiPdk() };

        WaveguideBendRadiusResolver.ResolveForEndpointPdkNames("Cornerstone SiN", "Cornerstone SiN", drafts)
            .ShouldBe(30);
        // SiEPIC-to-SiEPIC resolves BELOW the generic 10 µm fallback: the foundry declares
        // 5 µm legal, so the canvas-wide fallback no longer over-constrains the route.
        WaveguideBendRadiusResolver.ResolveForEndpointPdkNames("SiEPIC SOI", "SiEPIC SOI", drafts)
            .ShouldBe(5);
    }

    [Fact]
    public void ResolveForEndpointPdkNames_OneEndpointUnresolvable_UsesTheOtherEndpointsFloor()
    {
        var drafts = new List<PdkDraft> { CornerstoneSinPdk() };

        // Built-in / group / PDK-less components carry no PDK source (null).
        WaveguideBendRadiusResolver.ResolveForEndpointPdkNames("Cornerstone SiN", null, drafts)
            .ShouldBe(30);
        WaveguideBendRadiusResolver.ResolveForEndpointPdkNames(null, "Cornerstone SiN", drafts)
            .ShouldBe(30);
    }

    [Fact]
    public void ResolveForEndpointPdkNames_NeitherEndpointResolvable_ReturnsNull()
    {
        var drafts = new List<PdkDraft> { CornerstoneSinPdk() };

        WaveguideBendRadiusResolver.ResolveForEndpointPdkNames(null, null, drafts).ShouldBeNull();
        WaveguideBendRadiusResolver.ResolveForEndpointPdkNames("Unknown PDK", "Also Unknown", drafts)
            .ShouldBeNull();
        WaveguideBendRadiusResolver.ResolveForEndpointPdkNames("Cornerstone SiN", "Cornerstone SiN", null)
            .ShouldBeNull();
    }

    [Fact]
    public void ResolveForEndpointPdkNames_PdkWithoutOpticalMinimum_HasNoOpinion()
    {
        var noOpticalMin = new PdkDraft
        {
            Name = "NoMin PDK",
            Process = new ProcessDefinition
            {
                Name = "NoMin",
                Xsections = new List<ProcessXsection>
                {
                    new() { Name = "E200", Kind = XsectionKind.Optical, MinRadiusUm = 0 },
                    new() { Name = "MetalDC", Kind = XsectionKind.Metal, MinRadiusUm = 8 }
                }
            }
        };
        var drafts = new List<PdkDraft> { noOpticalMin, CornerstoneSinPdk() };

        WaveguideBendRadiusResolver.ResolveForEndpointPdkNames("NoMin PDK", "NoMin PDK", drafts)
            .ShouldBeNull();
        WaveguideBendRadiusResolver.ResolveForEndpointPdkNames("NoMin PDK", "Cornerstone SiN", drafts)
            .ShouldBe(30);
    }

    [Fact]
    public void ResolveForEndpointPdkNames_PdkNameMatchesCaseInsensitive()
    {
        var drafts = new List<PdkDraft> { CornerstoneSinPdk() };

        WaveguideBendRadiusResolver.ResolveForEndpointPdkNames("cornerstone sin", "CORNERSTONE SIN", drafts)
            .ShouldBe(30);
    }

    private static PdkDraft CornerstoneSinPdk() => new()
    {
        Name = "Cornerstone SiN",
        Process = new ProcessDefinition
        {
            Name = "Cornerstone SiN",
            Xsections = new List<ProcessXsection>
            {
                new() { Name = "sin300", Kind = XsectionKind.Optical, MinRadiusUm = 30 },
                new() { Name = "MetalDC", Kind = XsectionKind.Metal, MinRadiusUm = 5 }
            }
        }
    };

    private static PdkDraft SiepicSoiPdk() => new()
    {
        Name = "SiEPIC SOI",
        Process = new ProcessDefinition
        {
            Name = "SiEPIC SOI",
            Xsections = new List<ProcessXsection>
            {
                new() { Name = "strip", Kind = XsectionKind.Optical, MinRadiusUm = 5 }
            }
        }
    };
}
