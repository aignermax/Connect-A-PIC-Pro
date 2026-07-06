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
        // B=224 and C=216 are NOT mutually compatible (Δ8 > 5), so whatever partition
        // the (deterministically sorted) grouping picks, B and C must never share a
        // group — chained drift through A=220 must not bridge them.
        var groups = ProcessCatalog.BuildGroups(new[]
        {
            Pdk("A", "Si", 220, "SiO2", 1550),
            Pdk("B", "Si", 224, "SiO2", 1550),
            Pdk("C", "Si", 216, "SiO2", 1550),
        });
        groups.Count.ShouldBe(2);
        groups.ShouldAllBe(g =>
            !(g.MemberPdkNames.Contains("B") && g.MemberPdkNames.Contains("C")));
    }

    [Fact]
    public void BuildGroups_SamePdkSet_YieldsSameGroupsRegardlessOfInputOrder()
    {
        // Greedy tolerance grouping is order-dependent for chains (218/222/226 with ±5 nm:
        // 218+222 group, or 222+226, depending on which arrives first). Input order comes
        // from filesystem enumeration, which differs per machine — the catalog must sort
        // deterministically so the same PDK set always yields the same processes.
        var a = Pdk("A", "Si", 218, "SiO2", 1550);
        var b = Pdk("B", "Si", 222, "SiO2", 1550);
        var c = Pdk("C", "Si", 226, "SiO2", 1550);

        static string Key(System.Collections.Generic.IReadOnlyList<ProcessGroup> gs) =>
            string.Join(";", gs
                .Select(g => string.Join(",", g.MemberPdkNames.OrderBy(n => n)))
                .OrderBy(s => s));

        var reference = Key(ProcessCatalog.BuildGroups(new[] { a, b, c }));

        Key(ProcessCatalog.BuildGroups(new[] { c, b, a })).ShouldBe(reference);
        Key(ProcessCatalog.BuildGroups(new[] { b, c, a })).ShouldBe(reference);
        Key(ProcessCatalog.BuildGroups(new[] { c, a, b })).ShouldBe(reference);
    }
}
