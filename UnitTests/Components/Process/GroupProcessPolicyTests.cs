using CAP_Core.Components.Process;
using Shouldly;
using Xunit;

namespace UnitTests.Components.Process;

/// <summary>
/// Group-level single-process enforcement (issue #653): a group's process membership is
/// derived from its children's PDK sources, so one foreign-process child blocks the group.
/// </summary>
public class GroupProcessPolicyTests
{
    private static ActiveProcessSelection Soi(params string[] memberPdkNames) =>
        ActiveProcessSelection.ForGroup(new ProcessGroup(
            "SOI 220",
            new ProcessFingerprint("Si", 220, "SiO2", 1550, "SOI 220"),
            memberPdkNames));

    [Fact]
    public void AllChildrenFromMemberPdk_IsAllowed()
    {
        var (isAllowed, reason) = GroupProcessPolicy.CheckGroupPlacement(
            Soi("Demo"), new[] { "Demo", "Demo" });

        isAllowed.ShouldBeTrue();
        reason.ShouldBeNull();
    }

    [Fact]
    public void MixOfBuiltInAgnosticAndMemberChildren_IsAllowed()
    {
        var (isAllowed, _) = GroupProcessPolicy.CheckGroupPlacement(
            Soi("Demo"),
            new[] { null, "Built-in", "Analysis Tools", "Demo" },
            new[] { "Analysis Tools" });

        isAllowed.ShouldBeTrue();
    }

    [Fact]
    public void SingleForeignChild_BlocksWholeGroup_AndNamesOffenderAndProcess()
    {
        var (isAllowed, reason) = GroupProcessPolicy.CheckGroupPlacement(
            Soi("Demo"), new[] { "Demo", "HHI-InP" }, groupName: "MyMixer");

        isAllowed.ShouldBeFalse();
        reason.ShouldNotBeNull();
        reason!.ShouldContain("MyMixer");
        reason.ShouldContain("HHI-InP");
        reason.ShouldContain("SOI 220");
    }

    [Fact]
    public void MultipleForeignPdks_AreListedOnce_Each()
    {
        var (isAllowed, reason) = GroupProcessPolicy.CheckGroupPlacement(
            Soi("Demo"), new[] { "HHI-InP", "hhi-inp", "IMEC" });

        isAllowed.ShouldBeFalse();
        reason!.ShouldContain("HHI-InP");
        reason.ShouldContain("IMEC");
        // Case-insensitive duplicate collapsed to one mention.
        reason.Split("HHI-InP").Length.ShouldBe(2);
    }

    [Fact]
    public void NoActiveProcess_AllowsForeignChildren()
    {
        var (isAllowed, _) = GroupProcessPolicy.CheckGroupPlacement(
            null, new[] { "HHI-InP" });

        isAllowed.ShouldBeTrue();
    }

    [Fact]
    public void Playground_AllowsForeignChildren()
    {
        var (isAllowed, _) = GroupProcessPolicy.CheckGroupPlacement(
            ActiveProcessSelection.Playground(), new[] { "HHI-InP", "Demo" });

        isAllowed.ShouldBeTrue();
    }

    [Fact]
    public void EmptyGroup_IsAllowed()
    {
        var (isAllowed, _) = GroupProcessPolicy.CheckGroupPlacement(
            Soi("Demo"), Array.Empty<string?>());

        isAllowed.ShouldBeTrue();
    }

    /// <summary>
    /// A child from a PDK missing from the persisted snapshot but present in the live
    /// by-value member set (#732) must not block the group — mirrors
    /// <c>SingleProcessPolicyTests.PdkNotInSnapshot_ButInLiveMemberSet_IsAllowed</c> at the
    /// group level. The live set replaces the snapshot, so it lists ALL currently
    /// compatible PDKs (including the snapshot member "Demo").
    /// </summary>
    [Fact]
    public void ChildPdkOnlyInLiveMemberSet_IsAllowed()
    {
        var (isAllowed, _) = GroupProcessPolicy.CheckGroupPlacement(
            Soi("Demo"), new[] { "Demo", "MyLib" },
            liveMemberPdkNames: new[] { "Demo", "MyLib" });

        isAllowed.ShouldBeTrue();
    }

    /// <summary>A child PDK absent from the (authoritative) live set still blocks the group.</summary>
    [Fact]
    public void ChildPdkNotInLiveMemberSet_StillBlocksGroup()
    {
        var (isAllowed, reason) = GroupProcessPolicy.CheckGroupPlacement(
            Soi("Demo"), new[] { "Demo", "HHI-InP" },
            liveMemberPdkNames: new[] { "Demo", "MyLib" });

        isAllowed.ShouldBeFalse();
        reason.ShouldNotBeNull();
        reason!.ShouldContain("HHI-InP");
        // The live-member child must not be listed as an offender.
        reason.ShouldNotContain("'Demo'");
    }

    /// <summary>
    /// Group-level mirror of the replace-not-union rule: a snapshot-member child whose PDK
    /// dropped out of the live set (process edited incompatible after saving) blocks the group.
    /// </summary>
    [Fact]
    public void SnapshotMemberChild_MissingFromLiveSet_BlocksGroup()
    {
        var (isAllowed, reason) = GroupProcessPolicy.CheckGroupPlacement(
            Soi("Demo"), new[] { "Demo" },
            liveMemberPdkNames: new[] { "MyLib" });

        isAllowed.ShouldBeFalse();
        reason!.ShouldContain("Demo");
    }
}
