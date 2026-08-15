using CAP_Core.Components.Core;
using CAP_Core.Components.Process;
using Shouldly;
using Xunit;

namespace UnitTests.Components.Process;

/// <summary>
/// Per-chiplet process scope (issue #935, north-star #537 rung 6): a
/// <see cref="ComponentGroup"/> may carry its own <see cref="ComponentGroup.FabricationProcess"/>;
/// placement/paste checks then resolve against that chiplet's process instead of the
/// canvas-global <see cref="PlacementPolicyContext.ActiveProcess"/>. Two chiplets of
/// different processes can coexist on one canvas without dropping to Playground.
/// </summary>
public class ChipletPlacementScopeTests
{
    private static ActiveProcessSelection Soi(params string[] memberPdkNames) =>
        ActiveProcessSelection.ForGroup(new ProcessGroup(
            "SOI 220",
            new ProcessFingerprint("Si", 220, "SiO2", 1550, "SOI 220"),
            memberPdkNames));

    private static ActiveProcessSelection InP(params string[] memberPdkNames) =>
        ActiveProcessSelection.ForGroup(new ProcessGroup(
            "HHI-InP",
            new ProcessFingerprint("InP", 400, "InP", 1550, "HHI-InP"),
            memberPdkNames));

    private static ComponentGroup Chiplet(ActiveProcessSelection? process, string name = "Chiplet") =>
        new(name) { FabricationProcess = process };

    private static PlacementPolicyContext ContextFor(ActiveProcessSelection? canvasActive) =>
        new(() => canvasActive,
            () => Array.Empty<string>(),
            _ => null);

    [Fact]
    public void EffectiveProcess_ChipletWithBinding_WinsOverCanvas()
    {
        var context = ContextFor(Soi("Demo"));
        var chiplet = Chiplet(InP("InP-Lib"));

        context.EffectiveProcessFor(chiplet).ShouldBe(chiplet.FabricationProcess);
    }

    [Fact]
    public void EffectiveProcess_ChipletWithoutBinding_FallsBackToCanvas()
    {
        var canvas = Soi("Demo");
        var context = ContextFor(canvas);

        context.EffectiveProcessFor(Chiplet(null)).ShouldBe(canvas);
        context.EffectiveProcessFor(null).ShouldBe(canvas);
    }

    [Fact]
    public void PlaceComponent_MemberOfChipletProcess_AllowedThoughCanvasLockedElsewhere()
    {
        // Canvas is locked to SOI, the chiplet is an InP chiplet: an InP component drops
        // into the InP chiplet even though it would be rejected on the open canvas.
        var context = ContextFor(Soi("Demo"));
        var chiplet = Chiplet(InP("InP-Lib"));

        context.CheckPlacementInScope(chiplet, "InP-Lib").IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void PlaceComponent_ForeignToChipletProcess_BlockedThoughCanvasWouldAllow()
    {
        // Canvas is SOI and "Demo" is a member; the chiplet is InP. Dropping a "Demo"
        // component into the InP chiplet must be rejected against the chiplet, not the
        // canvas — otherwise a chiplet could be polluted with foreign-process parts.
        var context = ContextFor(Soi("Demo"));
        var chiplet = Chiplet(InP("InP-Lib"));

        var (isAllowed, reason) = context.CheckPlacementInScope(chiplet, "Demo");

        isAllowed.ShouldBeFalse();
        reason.ShouldNotBeNull();
        reason!.ShouldContain("Demo");
        reason.ShouldContain("HHI-InP");
    }

    [Fact]
    public void PlaceComponent_NoChipletScope_UsesCanvasGlobal()
    {
        var context = ContextFor(Soi("Demo"));

        context.CheckPlacementInScope(null, "Demo").IsAllowed.ShouldBeTrue();
        context.CheckPlacementInScope(null, "HHI-InP").IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void PlaceComponent_ChipletWithoutBinding_UsesCanvasGlobal()
    {
        var context = ContextFor(Soi("Demo"));

        context.CheckPlacementInScope(Chiplet(null), "Demo").IsAllowed.ShouldBeTrue();
        context.CheckPlacementInScope(Chiplet(null), "HHI-InP").IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void PlaceComponent_ChipletBoundToPlayground_AllowsAnything()
    {
        var context = ContextFor(Soi("Demo"));
        var chiplet = Chiplet(ActiveProcessSelection.Playground());

        context.CheckPlacementInScope(chiplet, "AnyForeignPdk").IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void PlaceGroup_AllChildrenMemberOfChipletProcess_Allowed()
    {
        var context = ContextFor(Soi("Demo"));
        var chiplet = Chiplet(InP("InP-Lib"));

        var (isAllowed, _) = context.CheckGroupPlacementInScope(
            chiplet, new[] { "InP-Lib", "InP-Lib" }, groupName: "InP pair");

        isAllowed.ShouldBeTrue();
    }

    [Fact]
    public void PlaceGroup_ForeignChildAgainstChiplet_BlockedEvenWhenCanvasMember()
    {
        var context = ContextFor(Soi("Demo"));
        var chiplet = Chiplet(InP("InP-Lib"));

        var (isAllowed, reason) = context.CheckGroupPlacementInScope(
            chiplet, new[] { "InP-Lib", "Demo" }, groupName: "Mixed");

        isAllowed.ShouldBeFalse();
        reason.ShouldNotBeNull();
        reason!.ShouldContain("Demo");
        reason.ShouldContain("Mixed");
    }

    [Fact]
    public void PlaceGroup_NoChipletScope_FallsBackToCanvasGlobal()
    {
        var context = ContextFor(Soi("Demo"));

        context.CheckGroupPlacementInScope(null, new[] { "Demo" }).IsAllowed.ShouldBeTrue();
        context.CheckGroupPlacementInScope(null, new[] { "HHI-InP" }).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void TwoChiplets_OfDifferentProcesses_CoexistOnOneCanvas()
    {
        // The north-star scenario: one canvas, two chiplets in two processes. Each accepts
        // only its own members; neither forces the design into Playground.
        var context = ContextFor(Soi("Demo"));
        var soiChiplet = Chiplet(Soi("Demo"), "SOI chiplet");
        var inPChiplet = Chiplet(InP("InP-Lib"), "InP chiplet");

        context.CheckPlacementInScope(soiChiplet, "Demo").IsAllowed.ShouldBeTrue();
        context.CheckPlacementInScope(soiChiplet, "InP-Lib").IsAllowed.ShouldBeFalse();

        context.CheckPlacementInScope(inPChiplet, "InP-Lib").IsAllowed.ShouldBeTrue();
        context.CheckPlacementInScope(inPChiplet, "Demo").IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void FabricationProcess_SurvivesDeepCopy()
    {
        // A chiplet copied or instantiated from a template must keep its binding —
        // otherwise the copy would silently revert to canvas-global enforcement.
        var chiplet = Chiplet(InP("InP-Lib"));

        var copy = chiplet.DeepCopy();

        copy.FabricationProcess.ShouldBe(chiplet.FabricationProcess);
    }
}
