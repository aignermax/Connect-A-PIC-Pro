namespace CAP.Avalonia.Controls.Rendering.LabelDeclutter;

/// <summary>
/// Pure priority/overlap resolution for canvas text labels: given every label's bounds and
/// priority, decides which ones actually get drawn. Labels stay visible for orientation by
/// default — only a label that visually collides with a higher-priority (or, on a tie,
/// deterministically earlier) one is dropped, so the canvas never shows two overlapping names
/// as illegible text soup.
/// </summary>
public static class LabelOverlapResolver
{
    /// <summary>
    /// Returns the <see cref="LabelCandidate.Id"/> of every candidate that should be drawn.
    /// Candidates are considered highest-priority first (ties broken by ascending <see
    /// cref="LabelCandidate.Id"/> — a component's stable Guid, not its display name — for a
    /// deterministic and flicker-free result); each one is kept unless its bounds intersect a
    /// candidate already accepted, so a lower-priority label can still be hidden by another
    /// lower-priority label that was accepted first only because it happened to win the
    /// tie-break — matching "the rest" all being equally unimportant among themselves.
    /// </summary>
    /// <param name="candidates">Every label under consideration this pass.</param>
    public static IReadOnlySet<Guid> ResolveVisibleLabels(IReadOnlyList<LabelCandidate> candidates)
    {
        var ordered = candidates
            .OrderByDescending(c => c.Priority)
            .ThenBy(c => c.Id)
            .ToList();

        var accepted = new List<LabelCandidate>(ordered.Count);
        var visibleIds = new HashSet<Guid>(ordered.Count);

        foreach (var candidate in ordered)
        {
            if (accepted.Exists(a => a.Bounds.Intersects(candidate.Bounds)))
                continue;

            accepted.Add(candidate);
            visibleIds.Add(candidate.Id);
        }

        return visibleIds;
    }
}
