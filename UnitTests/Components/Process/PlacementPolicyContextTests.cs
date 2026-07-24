using CAP_Core.Components.Process;
using Shouldly;
using Xunit;

namespace UnitTests.Components.Process;

/// <summary>
/// Verifies the shared placement-policy context (issue #737): live evaluation of its
/// accessors, delegation to the single/group process policies, and the unrestricted
/// default that mirrors the previous "unwired func → allow" fallback.
/// </summary>
public class PlacementPolicyContextTests
{
    private static ActiveProcessSelection Soi() => ActiveProcessSelection.ForGroup(
        new ProcessGroup("SOI 220", new ProcessFingerprint("Si", 220, "SiO2", 1550, "SOI 220"),
            new[] { "Demo" }));

    private static PlacementPolicyContext SoiContext(params string[] agnosticPdkNames) =>
        new(() => Soi(), () => agnosticPdkNames, _ => null);

    [Fact]
    public void CheckPlacement_DelegatesToSingleProcessPolicy()
    {
        SoiContext().CheckPlacement("Demo").IsAllowed.ShouldBeTrue();

        var (ok, reason) = SoiContext().CheckPlacement("HHI-InP");
        ok.ShouldBeFalse();
        reason!.ShouldContain("HHI-InP");
    }

    [Fact]
    public void CheckPlacement_HonorsProcessAgnosticPdkNames()
    {
        SoiContext("Analysis Tools").CheckPlacement("Analysis Tools").IsAllowed.ShouldBeTrue();
        SoiContext().CheckPlacement("Analysis Tools").IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void CheckGroupPlacement_DelegatesToGroupProcessPolicy()
    {
        SoiContext().CheckGroupPlacement(new[] { "Demo", null }).IsAllowed.ShouldBeTrue();

        var (ok, reason) = SoiContext().CheckGroupPlacement(new[] { "Demo", "HHI-InP" }, "Mixer");
        ok.ShouldBeFalse();
        reason!.ShouldContain("Mixer");
        reason.ShouldContain("HHI-InP");
    }

    [Fact]
    public void Accessors_EvaluateLive_NotAtConstructionTime()
    {
        ActiveProcessSelection? active = null;
        var context = new PlacementPolicyContext(() => active, () => Array.Empty<string>(), _ => null);

        context.CheckPlacement("HHI-InP").IsAllowed.ShouldBeTrue("no process locked in yet");

        active = Soi();
        context.CheckPlacement("HHI-InP").IsAllowed.ShouldBeFalse("the context must see the later lock-in");
    }

    [Fact]
    public void Unrestricted_AllowsAnythingAndResolvesToBuiltIn()
    {
        var context = PlacementPolicyContext.Unrestricted;

        context.ActiveProcess.ShouldBeNull();
        context.ProcessAgnosticPdkNames.ShouldBeEmpty();
        context.CheckPlacement("HHI-InP").IsAllowed.ShouldBeTrue();
        context.CheckGroupPlacement(new[] { "HHI-InP" }).IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void Constructor_RejectsNullAccessors()
    {
        Should.Throw<ArgumentNullException>(() =>
            new PlacementPolicyContext(null!, () => Array.Empty<string>(), _ => null));
        Should.Throw<ArgumentNullException>(() =>
            new PlacementPolicyContext(() => null, null!, _ => null));
        Should.Throw<ArgumentNullException>(() =>
            new PlacementPolicyContext(() => null, () => Array.Empty<string>(), null!));
    }
}
