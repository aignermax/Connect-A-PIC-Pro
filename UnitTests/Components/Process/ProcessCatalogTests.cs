using System.Linq;
using CAP_Core.Components.Process;
using Shouldly;
using Xunit;

namespace UnitTests.Components.Process;

public class ProcessCatalogTests
{
    private static PdkProcessEntry Pdk(string name, string? mat, double? thick, string? clad, int wl, string? proc = null) =>
        new(name, new ProcessFingerprint(mat, thick, clad, wl, proc));

    [Fact]
    public void CompatiblePdks_CollapseIntoOneGroup()
    {
        var groups = ProcessCatalog.BuildGroups(new[]
        {
            Pdk("Foundry SOI", "Si", 220, "SiO2", 1550, "SOI 220"),
            Pdk("Custom SOI",  "Si", 221, "SiO2", 1555),
        });

        groups.Count.ShouldBe(1);
        groups[0].MemberPdkNames.ShouldBe(new[] { "Foundry SOI", "Custom SOI" }, ignoreOrder: true);
    }

    [Fact]
    public void IncompatiblePdks_FormSeparateGroups()
    {
        var groups = ProcessCatalog.BuildGroups(new[]
        {
            Pdk("SOI",  "Si",  220, "SiO2", 1550),
            Pdk("SiNx", "SiN", 340, "SiO2", 1550),
        });
        groups.Count.ShouldBe(2);
    }

    [Fact]
    public void UnspecifiedPdk_IsItsOwnSingletonGroup()
    {
        var groups = ProcessCatalog.BuildGroups(new[]
        {
            Pdk("Legacy A", null, null, null, 1550),
            Pdk("Legacy B", null, null, null, 1550),
        });
        groups.Count.ShouldBe(2);   // never merge unspecified fingerprints
    }

    [Fact]
    public void GroupDisplayName_PrefersSharedProcessName()
    {
        var groups = ProcessCatalog.BuildGroups(new[] { Pdk("P", "Si", 220, "SiO2", 1550, "AMF SOI 220nm") });
        groups[0].DisplayName.ShouldBe("AMF SOI 220nm");
    }

    [Fact]
    public void ChainedToleranceDrift_DoesNotGroupNonMutuallyCompatiblePdks()
    {
        // A=220 & B=224 are mutually compatible (Δ4≤5). C=216 is compatible with A (Δ4)
        // but NOT with B (Δ8>5), so C must not share the A+B group.
        var groups = ProcessCatalog.BuildGroups(new[]
        {
            Pdk("A", "Si", 220, "SiO2", 1550),
            Pdk("B", "Si", 224, "SiO2", 1550),
            Pdk("C", "Si", 216, "SiO2", 1550),
        });
        groups.Count.ShouldBe(2);
        groups.Single(g => g.MemberPdkNames.Contains("C")).MemberPdkNames.ShouldBe(new[] { "C" });
    }
}
