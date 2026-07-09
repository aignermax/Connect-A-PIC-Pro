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
}
