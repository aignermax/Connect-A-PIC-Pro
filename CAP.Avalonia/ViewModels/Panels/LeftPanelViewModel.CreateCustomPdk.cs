namespace CAP.Avalonia.ViewModels.Panels;

/// <summary>
/// Registration entry point for the PDK-Management panel's "+" button (issue #700 follow-up,
/// LC-T2): the button opens <c>CreateCustomPdkWindow</c> directly (no "New Component" detour),
/// and on success this method takes the freshly saved — possibly component-less — PDK file
/// straight into the loaded-PDK list so it shows up immediately, without waiting for the next
/// app restart's directory scan (<see cref="LeftPanelViewModel.ReloadUserPdksAtStartupAsync"/>,
/// issue #700). Split into its own partial purely to keep <c>LeftPanelViewModel.cs</c> under the
/// project's line-count limit.
/// </summary>
public partial class LeftPanelViewModel
{
    /// <summary>
    /// Loads the PDK at <paramref name="filePath"/> and registers it into the library exactly
    /// like a single-file entry of <see cref="ReloadUserPdksAtStartupAsync"/> would (same
    /// <see cref="TryReloadUserPdk"/> helper — a load failure or corrupt file is tolerated and
    /// logged rather than crashing, and a name collision with an already-loaded PDK is skipped as
    /// a tolerated duplicate), then re-applies the active process lock and re-filters so the new
    /// PDK's visibility (and its components', if any) is correct immediately. A path already
    /// loaded (e.g. a second click before the window closed, or the caller registering the same
    /// path twice) is a no-op, mirroring the startup reload's own dedupe.
    /// </summary>
    /// <param name="filePath">Full path to the just-created PDK JSON file.</param>
    internal void RegisterCreatedPdk(string filePath)
    {
        if (PdkManager.IsPdkLoaded(filePath))
            return;

        TryReloadUserPdk(filePath);

        ReapplyActiveProcessAfterPdkChange();
        FilterComponents();
    }
}
