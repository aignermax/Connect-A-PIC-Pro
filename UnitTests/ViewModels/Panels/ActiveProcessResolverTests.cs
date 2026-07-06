using System.Collections.Generic;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Process;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels.Panels;

public class ActiveProcessResolverTests
{
    private static ProcessGroup Soi => new("SOI 220",
        new ProcessFingerprint("Si", 220, "SiO2", 1550, "SOI 220"), new[] { "Demo", "SiEPIC" });
    private static ProcessGroup Inp => new("InP",
        new ProcessFingerprint("InP", 300, "InP", 1550, "InP"), new[] { "HHI" });

    [Fact]
    public void RoundTrip_RealProcess_PreservesMembersAndName()
    {
        var sel = ActiveProcessSelection.ForGroup(Soi);
        var back = ActiveProcessResolver.FromData(ActiveProcessResolver.ToData(sel));
        back!.DisplayName.ShouldBe("SOI 220");
        back.MemberPdkNames.ShouldBe(new[] { "Demo", "SiEPIC" });
        back.IsPlayground.ShouldBeFalse();
    }

    [Fact]
    public void RoundTrip_Playground_IsPreserved()
    {
        var back = ActiveProcessResolver.FromData(
            ActiveProcessResolver.ToData(ActiveProcessSelection.Playground()));
        back!.IsPlayground.ShouldBeTrue();
    }

    [Fact]
    public void Migrate_AllComponentsOneGroup_AdoptsThatProcess()
    {
        var sel = ActiveProcessResolver.Migrate(
            new[] { "Demo", "SiEPIC", null }, new[] { Soi, Inp }, out var warning);
        sel!.DisplayName.ShouldBe("SOI 220");
        warning.ShouldBeNull();
    }

    [Fact]
    public void Migrate_ComponentsSpanGroups_FallsBackToPlaygroundWithWarning()
    {
        var sel = ActiveProcessResolver.Migrate(
            new[] { "Demo", "HHI" }, new[] { Soi, Inp }, out var warning);
        sel!.IsPlayground.ShouldBeTrue();
        warning.ShouldNotBeNull();
        warning!.ShouldContain("SOI 220");
        warning.ShouldContain("InP");
    }

    [Fact]
    public void Migrate_ComponentsFromUninstalledPdk_FallsBackToPlaygroundNamingMissingPdk()
    {
        var sel = ActiveProcessResolver.Migrate(
            new[] { "Ghost PDK" }, new[] { Soi, Inp }, out var warning);
        sel!.IsPlayground.ShouldBeTrue();
        warning.ShouldNotBeNull();
        warning!.ShouldContain("Ghost PDK");
        warning.ShouldNotContain("multiple processes");
    }

    [Fact]
    public void Migrate_RealProcessPlusAgnosticTool_ExcludesAgnosticAndAdoptsProcess()
    {
        var sel = ActiveProcessResolver.Migrate(
            new[] { "Demo", "Analysis Tools" }, new[] { Soi, Inp }, out var warning,
            processAgnosticPdkNames: new[] { "Analysis Tools" });

        sel!.DisplayName.ShouldBe("SOI 220");
        sel.IsPlayground.ShouldBeFalse();
        warning.ShouldBeNull();
    }

    [Fact]
    public void Migrate_OnlyAgnosticTool_ReturnsNullWithNoWarning()
    {
        var sel = ActiveProcessResolver.Migrate(
            new[] { "Analysis Tools" }, new[] { Soi, Inp }, out var warning,
            processAgnosticPdkNames: new[] { "Analysis Tools" });

        sel.ShouldBeNull();
        warning.ShouldBeNull();
    }

    [Fact]
    public void Migrate_MixedInstalledAndUninstalledPdk_AdoptsProcessButWarnsAboutUncovered()
    {
        // One installed group matches, but the design ALSO contains components from a
        // PDK no installed process covers — silently claiming manufacturability under
        // SOI 220 would be wrong, so the adoption must carry a warning.
        var sel = ActiveProcessResolver.Migrate(
            new[] { "Demo", "Ghost PDK" }, new[] { Soi, Inp }, out var warning);

        sel!.DisplayName.ShouldBe("SOI 220");
        warning.ShouldNotBeNull();
        warning!.ShouldContain("Ghost PDK");
    }

    // ─── Revalidate (stored processes re-anchored to the installed catalog) ─────

    [Fact]
    public void Revalidate_Playground_PassesThroughWithoutWarning()
    {
        var result = ActiveProcessResolver.Revalidate(
            ActiveProcessSelection.Playground(), new[] { Soi }, out var warning);

        result.IsPlayground.ShouldBeTrue();
        warning.ShouldBeNull();
    }

    [Fact]
    public void Revalidate_CompatibleCatalogGroup_AdoptsFreshMemberList()
    {
        // Stored snapshot only knew "Demo"; a compatible PDK ("SiEPIC") was installed
        // since. Re-anchoring must include it, otherwise the frozen save-time list
        // blocks placement from a PDK that belongs to the very same process.
        var stored = new ActiveProcessSelection("SOI 220 (old)",
            new ProcessFingerprint("Si", 221, "SiO2", 1552, null),
            new[] { "Demo" }, IsPlayground: false);

        var result = ActiveProcessResolver.Revalidate(stored, new[] { Soi, Inp }, out var warning);

        warning.ShouldBeNull();
        result.MemberPdkNames.ShouldBe(new[] { "Demo", "SiEPIC" }, ignoreOrder: true);
    }

    [Fact]
    public void Revalidate_NoInstalledPdkMatches_KeepsSelectionAndWarns()
    {
        var stored = new ActiveProcessSelection("Foreign fab",
            new ProcessFingerprint("GaAs", 500, "AlGaAs", 900, null),
            new[] { "GhostPdk" }, IsPlayground: false);

        var result = ActiveProcessResolver.Revalidate(stored, new[] { Soi }, out var warning);

        result.DisplayName.ShouldBe("Foreign fab");
        warning.ShouldNotBeNull();
        warning!.ShouldContain("GhostPdk");
    }
}
