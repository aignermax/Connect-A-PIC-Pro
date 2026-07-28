using Avalonia;
using CAP.Avalonia.Controls.Rendering.LabelDeclutter;
using Shouldly;
using Xunit;

namespace UnitTests.Controls.Rendering.LabelDeclutter;

/// <summary>
/// Tests for <see cref="LabelOverlapResolver.ResolveVisibleLabels"/>: a pure priority/overlap
/// function (list of label bounds + priority in, visibility set out) with no rendering or
/// state, so every case here is a plain input/output assertion.
/// </summary>
public class LabelOverlapResolverTests
{
    private static readonly Rect BoundsA = new(0, 0, 40, 16);
    private static readonly Rect BoundsOverlappingA = new(20, 0, 40, 16);
    private static readonly Rect BoundsFarAway = new(500, 500, 40, 16);

    [Fact]
    public void NonOverlappingLabels_AreAllVisible()
    {
        var candidates = new[]
        {
            new LabelCandidate("a", BoundsA, LabelPriority.Normal),
            new LabelCandidate("b", BoundsFarAway, LabelPriority.Normal),
        };

        var visible = LabelOverlapResolver.ResolveVisibleLabels(candidates);

        visible.ShouldBe(new[] { "a", "b" }, ignoreOrder: true);
    }

    [Fact]
    public void OverlappingLabels_SelectedBeatsNormal()
    {
        var candidates = new[]
        {
            new LabelCandidate("normal", BoundsA, LabelPriority.Normal),
            new LabelCandidate("selected", BoundsOverlappingA, LabelPriority.Selected),
        };

        var visible = LabelOverlapResolver.ResolveVisibleLabels(candidates);

        visible.ShouldBe(new[] { "selected" });
    }

    [Fact]
    public void OverlappingLabels_HoveredBeatsNormal()
    {
        var candidates = new[]
        {
            new LabelCandidate("normal", BoundsA, LabelPriority.Normal),
            new LabelCandidate("hovered", BoundsOverlappingA, LabelPriority.Hovered),
        };

        var visible = LabelOverlapResolver.ResolveVisibleLabels(candidates);

        visible.ShouldBe(new[] { "hovered" });
    }

    [Fact]
    public void OverlappingLabels_SelectedBeatsHovered()
    {
        var candidates = new[]
        {
            new LabelCandidate("hovered", BoundsA, LabelPriority.Hovered),
            new LabelCandidate("selected", BoundsOverlappingA, LabelPriority.Selected),
        };

        var visible = LabelOverlapResolver.ResolveVisibleLabels(candidates);

        visible.ShouldBe(new[] { "selected" });
    }

    [Fact]
    public void OverlappingEqualPriority_TieBreaksByOrdinalId_Deterministically()
    {
        var candidates = new[]
        {
            new LabelCandidate("zzz", BoundsA, LabelPriority.Normal),
            new LabelCandidate("aaa", BoundsOverlappingA, LabelPriority.Normal),
        };

        // Run twice with the input list in different orders — the result must not depend on
        // input order or which candidate happened to come first (no flicker as the pointer moves).
        LabelOverlapResolver.ResolveVisibleLabels(candidates).ShouldBe(new[] { "aaa" });
        LabelOverlapResolver.ResolveVisibleLabels(candidates.Reverse().ToArray()).ShouldBe(new[] { "aaa" });
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
            new LabelCandidate("a", a, LabelPriority.Selected),
            new LabelCandidate("b", b, LabelPriority.Normal),
            new LabelCandidate("c", c, LabelPriority.Normal),
        };

        var visible = LabelOverlapResolver.ResolveVisibleLabels(candidates);

        visible.ShouldBe(new[] { "a", "c" }, ignoreOrder: true);
    }

    [Fact]
    public void EmptyCandidateList_ReturnsEmptySet()
    {
        var visible = LabelOverlapResolver.ResolveVisibleLabels(System.Array.Empty<LabelCandidate>());

        visible.ShouldBeEmpty();
    }
}
