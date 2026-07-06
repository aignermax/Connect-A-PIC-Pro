using CAP_Core.Components.Process;
using Shouldly;
using Xunit;

namespace UnitTests.Components.Process;

public class SingleProcessPolicyTests
{
    private static ActiveProcessSelection Soi() => ActiveProcessSelection.ForGroup(
        new ProcessGroup("SOI 220", new ProcessFingerprint("Si", 220, "SiO2", 1550, "SOI 220"),
            new[] { "Foundry SOI", "Custom SOI" }));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Built-in")]
    public void BuiltInComponent_IsAlwaysAllowed(string? pdk)
    {
        SingleProcessPolicy.CheckPlacement(Soi(), pdk).IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void MemberPdk_IsAllowed()
    {
        SingleProcessPolicy.CheckPlacement(Soi(), "Custom SOI").IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void ForeignPdk_IsBlockedWithReason()
    {
        var (ok, reason) = SingleProcessPolicy.CheckPlacement(Soi(), "HHI-InP");
        ok.ShouldBeFalse();
        reason.ShouldNotBeNull();
        reason!.ShouldContain("HHI-InP");
        reason.ShouldContain("SOI 220");
    }

    [Fact]
    public void Playground_AllowsAnything()
    {
        SingleProcessPolicy.CheckPlacement(ActiveProcessSelection.Playground(), "HHI-InP").IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void NoActiveProcess_AllowsAnything()
    {
        SingleProcessPolicy.CheckPlacement(null, "HHI-InP").IsAllowed.ShouldBeTrue();
    }
}
