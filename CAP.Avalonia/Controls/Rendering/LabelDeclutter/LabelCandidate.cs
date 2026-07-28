using Avalonia;

namespace CAP.Avalonia.Controls.Rendering.LabelDeclutter;

/// <summary>
/// One label under consideration by <see cref="LabelOverlapResolver"/>: its stable identity,
/// its on-screen (or world-space, under a uniform zoom transform — overlap is scale-invariant)
/// bounds, and the priority it should be granted when it overlaps another label.
/// </summary>
/// <param name="Id">
/// Stable identity of the label's owner — a component's <c>Component.Id</c> (Guid), not its
/// mutable, user-editable name — also used as the deterministic tie-breaker between
/// equal-priority candidates so the same overlap always resolves the same way, no flicker as
/// the pointer moves.
/// </param>
/// <param name="Bounds">The label's measured text bounds.</param>
/// <param name="Priority">The label's claim on contested space; see <see cref="LabelPriority"/>.</param>
public readonly record struct LabelCandidate(Guid Id, Rect Bounds, LabelPriority Priority);
