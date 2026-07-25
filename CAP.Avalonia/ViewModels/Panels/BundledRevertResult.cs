namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// Outcome of reverting a fork component to its bundled foundry definition.
/// Tri-state because "nothing happened" splits into two cases the caller must
/// distinguish: there was nothing to revert (fall through to the normal flow)
/// vs. the revert was attempted and FAILED (report failure, never fall through —
/// a failed revert must not degrade into a delete or a fake success message).
/// </summary>
public enum BundledRevertResult
{
    /// <summary>The component was reverted to the bundled original; the library shows it again.</summary>
    Reverted,

    /// <summary>Not a revert case (no loaded fork PDK, or no bundled counterpart) — caller proceeds normally.</summary>
    NotARevertCase,

    /// <summary>The revert was attempted but the fork file could not be rewritten (already logged).</summary>
    Failed,
}
