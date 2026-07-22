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

    [Fact]
    public void ProcessAgnosticPdk_IsAllowedUnderRealProcess()
    {
        var agnostic = new[] { "Analysis Tools" };
        SingleProcessPolicy.CheckPlacement(Soi(), "Analysis Tools", agnostic).IsAllowed.ShouldBeTrue();
        // Without the agnostic set it stays blocked (default overload behavior unchanged):
        SingleProcessPolicy.CheckPlacement(Soi(), "Analysis Tools").IsAllowed.ShouldBeFalse();
    }

    /// <summary>
    /// A custom PDK registered after the process was saved cannot be in the persisted
    /// <see cref="ActiveProcessSelection.MemberPdkNames"/> snapshot, but is value-compatible
    /// with the active process, so the live-membership set (#732) resolved by
    /// <c>LeftPanelViewModel.ResolveLiveMemberPdkNames</c> must still allow it.
    /// </summary>
    [Fact]
    public void PdkNotInSnapshot_ButInLiveMemberSet_IsAllowed()
    {
        var live = new[] { "MyLib" };
        SingleProcessPolicy.CheckPlacement(Soi(), "MyLib", liveMemberPdkNames: live)
            .IsAllowed.ShouldBeTrue();
    }

    /// <summary>
    /// The live set REPLACES the snapshot, it is not unioned with it: a snapshot member whose
    /// process was edited into incompatibility AFTER the design was saved disappears from the
    /// live set and must then be blocked at placement/paste too — otherwise the library filter
    /// (live-only) and the placement gate would disagree about the same PDK.
    /// </summary>
    [Fact]
    public void SnapshotMemberPdk_MissingFromLiveMemberSet_IsBlocked()
    {
        // "Custom SOI" IS in the Soi() snapshot, but not in the live set anymore.
        var live = new[] { "Foundry SOI" };
        var (ok, reason) = SingleProcessPolicy.CheckPlacement(Soi(), "Custom SOI", liveMemberPdkNames: live);

        ok.ShouldBeFalse("live membership is authoritative — the stale snapshot must not resurrect an edited-incompatible PDK");
        reason.ShouldNotBeNull();
        reason!.ShouldContain("Custom SOI");
    }

    /// <summary>
    /// A PDK absent from both the persisted snapshot and the live member set (i.e. genuinely
    /// value-incompatible with the active process) must remain blocked with the usual reason.
    /// </summary>
    [Fact]
    public void PdkNotInSnapshot_AndNotInLiveMemberSet_IsBlocked()
    {
        var live = new[] { "MyLib" };
        var (ok, reason) = SingleProcessPolicy.CheckPlacement(Soi(), "HHI-InP", liveMemberPdkNames: live);

        ok.ShouldBeFalse();
        reason.ShouldNotBeNull();
        reason!.ShouldContain("HHI-InP");
        reason.ShouldContain("SOI 220");
    }

    /// <summary>Live-member matching is case-insensitive, like the snapshot check.</summary>
    [Fact]
    public void LiveMemberSet_MatchIsCaseInsensitive()
    {
        SingleProcessPolicy.CheckPlacement(Soi(), "mylib", liveMemberPdkNames: new[] { "MyLib" })
            .IsAllowed.ShouldBeTrue();
    }

    /// <summary>A null live-member set (unwired callback) must behave exactly like before.</summary>
    [Fact]
    public void NoLiveMemberSet_FallsBackToSnapshotOnly()
    {
        SingleProcessPolicy.CheckPlacement(Soi(), "Custom SOI", liveMemberPdkNames: null)
            .IsAllowed.ShouldBeTrue("still allowed via the persisted snapshot");
        SingleProcessPolicy.CheckPlacement(Soi(), "HHI-InP", liveMemberPdkNames: null)
            .IsAllowed.ShouldBeFalse("no live set provided — foreign PDK stays blocked");
    }
}
