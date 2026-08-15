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
    public void ResolveForEndpointPdks_SamePdkPair_ResolvesThatPdksMinimum()
    {
        var pdks = new List<PdkDraft>
        {
            PdkWithOpticalMinimum("Cornerstone", 30),
            PdkWithOpticalMinimum("SiEPIC", 5),
        };

        WaveguideBendRadiusResolver.ResolveForEndpointPdks("SiEPIC", "SiEPIC", pdks, fallback: 10)
            .ShouldBe(5, "a same-chiplet pair is not diluted by the other PDK's looser minimum");
    }

    [Fact]
    public void ResolveForEndpointPdks_CrossProcessPair_StricterFloorWins()
    {
        var pdks = new List<PdkDraft>
        {
            PdkWithOpticalMinimum("Cornerstone", 30),
            PdkWithOpticalMinimum("SiEPIC", 5),
        };

        WaveguideBendRadiusResolver.ResolveForEndpointPdks("Cornerstone", "SiEPIC", pdks, fallback: 10)
            .ShouldBe(30, "the cross-process route must never undercut the tighter chiplet's floor");
        WaveguideBendRadiusResolver.ResolveForEndpointPdks("SiEPIC", "Cornerstone", pdks, fallback: 10)
            .ShouldBe(30, "pin order does not matter");
    }

    [Fact]
    public void ResolveForEndpointPdks_OneEndpointUnknown_KeepsTheOthersMinimum()
    {
        var pdks = new List<PdkDraft> { PdkWithOpticalMinimum("Cornerstone", 30) };

        WaveguideBendRadiusResolver.ResolveForEndpointPdks("Cornerstone", null, pdks, fallback: 10)
            .ShouldBe(30);
        WaveguideBendRadiusResolver.ResolveForEndpointPdks("Cornerstone", "NotLoaded", pdks, fallback: 10)
            .ShouldBe(30);
    }

    [Fact]
    public void ResolveForEndpointPdks_PdkWithoutOpticalMinimum_ContributesNothing()
    {
        var pdks = new List<PdkDraft>
        {
            PdkWithOpticalMinimum("Cornerstone", 30),
            new PdkDraft { Name = "NoMin", Process = new ProcessDefinition { Name = "NoMin" } },
        };

        WaveguideBendRadiusResolver.ResolveForEndpointPdks("NoMin", "Cornerstone", pdks, fallback: 10)
            .ShouldBe(30, "the endpoint that declares no minimum must not drag the floor down");
        WaveguideBendRadiusResolver.ResolveForEndpointPdks("NoMin", "NoMin", pdks, fallback: 10)
            .ShouldBe(10, "with no declared minimum on either side the fallback applies");
    }

    [Fact]
    public void ResolveForEndpointPdks_NeitherEndpointResolves_ReturnsFallback()
    {
        var pdks = new List<PdkDraft> { PdkWithOpticalMinimum("Cornerstone", 30) };

        WaveguideBendRadiusResolver.ResolveForEndpointPdks(null, null, pdks, fallback: 10).ShouldBe(10);
        WaveguideBendRadiusResolver.ResolveForEndpointPdks("A", "B", null, fallback: 10).ShouldBe(10);
    }

    [Fact]
    public void ResolveForEndpointPdks_NameMatchIsCaseInsensitive()
    {
        var pdks = new List<PdkDraft> { PdkWithOpticalMinimum("Cornerstone", 30) };

        WaveguideBendRadiusResolver.ResolveForEndpointPdks("cornerstone", "CORNERSTONE", pdks, fallback: 10)
            .ShouldBe(30);
    }

    private static PdkDraft PdkWithOpticalMinimum(string name, double minRadiusUm) =>
        new()
        {
            Name = name,
            Process = new ProcessDefinition
            {
                Name = name,
                Xsections = new List<ProcessXsection>
                {
                    new() { Name = "Strip", Kind = XsectionKind.Optical, MinRadiusUm = minRadiusUm }
                }
            }
        };
}
