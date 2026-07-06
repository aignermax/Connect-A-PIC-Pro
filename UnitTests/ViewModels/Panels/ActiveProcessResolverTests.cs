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
}
