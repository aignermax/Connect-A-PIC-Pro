using Avalonia;
using CAP.Avalonia.Controls.Rendering.LabelDeclutter;
using Shouldly;
using Xunit;

namespace UnitTests.Controls.Rendering.LabelDeclutter;

/// <summary>
/// Tests for <see cref="LabelOverlapResolver.ResolveVisibleLabels"/>: a pure priority/overlap
/// function (list of label bounds + priority in, visibility set out) with no rendering or
/// state, so every case here is a plain input/output assertion. Ids are fixed, low-valued Guids
/// (not the runtime-random <c>Component.Id</c>) purely so test expectations can name a specific
/// tie-break winner deterministically.
/// </summary>
public class LabelOverlapResolverTests
{
    private static readonly Guid IdA = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid IdB = new("00000000-0000-0000-0000-000000000002");
    private static readonly Guid IdC = new("00000000-0000-0000-0000-000000000003");

    private static readonly Rect BoundsA = new(0, 0, 40, 16);
    private static readonly Rect BoundsOverlappingA = new(20, 0, 40, 16);
    private static readonly Rect BoundsFarAway = new(500, 500, 40, 16);

    [Fact]
    public void NonOverlappingLabels_AreAllVisible()
    {
        var candidates = new[]
        {
            new LabelCandidate(IdA, BoundsA, LabelPriority.Normal),
            new LabelCandidate(IdB, BoundsFarAway, LabelPriority.Normal),
        };

        var visible = LabelOverlapResolver.ResolveVisibleLabels(candidates);

        visible.ShouldBe(new[] { IdA, IdB }, ignoreOrder: true);
    }

    [Fact]
    public void OverlappingLabels_SelectedBeatsNormal()
    {
        var candidates = new[]
        {
            new LabelCandidate(IdA, BoundsA, LabelPriority.Normal),
            new LabelCandidate(IdB, BoundsOverlappingA, LabelPriority.Selected),
        };

        var visible = LabelOverlapResolver.ResolveVisibleLabels(candidates);

        visible.ShouldBe(new[] { IdB });
    }

    [Fact]
    public void OverlappingLabels_HoveredBeatsNormal()
    {
        var candidates = new[]
        {
            new LabelCandidate(IdA, BoundsA, LabelPriority.Normal),
            new LabelCandidate(IdB, BoundsOverlappingA, LabelPriority.Hovered),
        };

        var visible = LabelOverlapResolver.ResolveVisibleLabels(candidates);

        visible.ShouldBe(new[] { IdB });
    }

    [Fact]
    public void OverlappingLabels_SelectedBeatsHovered()
    {
        var candidates = new[]
        {
            new LabelCandidate(IdA, BoundsA, LabelPriority.Hovered),
            new LabelCandidate(IdB, BoundsOverlappingA, LabelPriority.Selected),
        };

        var visible = LabelOverlapResolver.ResolveVisibleLabels(candidates);

        visible.ShouldBe(new[] { IdB });
    }

    [Fact]
    public void OverlappingEqualPriority_TieBreaksByAscendingId_Deterministically()
    {
        // IdA < IdB, so IdA wins regardless of input order — no flicker as the pointer moves.
        var candidates = new[]
        {
            new LabelCandidate(IdB, BoundsA, LabelPriority.Normal),
            new LabelCandidate(IdA, BoundsOverlappingA, LabelPriority.Normal),
        };

        LabelOverlapResolver.ResolveVisibleLabels(candidates).ShouldBe(new[] { IdA });
        LabelOverlapResolver.ResolveVisibleLabels(candidates.Reverse().ToArray()).ShouldBe(new[] { IdA });
    }

    [Fact]
    public void RejectedLabel_DoesNotBlockAFurtherLabelItOverlaps()
    {
        // a: x[0,40] (Selected, wins). b: x[30,70] overlaps a, so b is rejected. c: x[65,105]
        // overlaps b but NOT a. Since c is only checked against ACCEPTED labels (a), and a
        // and c don't overlap, c must still be visible — a rejected label must not transitively
        // hide something it never actually collides with on screen.
        var a = new Rect(0, 0, 40, 16);
        var b = new Rect(30, 0, 40, 16);
        var c = new Rect(65, 0, 40, 16);
        var candidates = new[]
        {
            new LabelCandidate(IdA, a, LabelPriority.Selected),
            new LabelCandidate(IdB, b, LabelPriority.Normal),
            new LabelCandidate(IdC, c, LabelPriority.Normal),
        };

        var visible = LabelOverlapResolver.ResolveVisibleLabels(candidates);

        visible.ShouldBe(new[] { IdA, IdC }, ignoreOrder: true);
    }

    [Fact]
    public void EmptyCandidateList_ReturnsEmptySet()
    {
        var visible = LabelOverlapResolver.ResolveVisibleLabels(Array.Empty<LabelCandidate>());

        visible.ShouldBeEmpty();
    }
}
