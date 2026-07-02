namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// Contract for a settings page in the Settings window.
/// Implement this interface to add a new settings category without modifying
/// <see cref="SettingsWindowViewModel"/>. Register via DI as
/// <c>services.AddTransient&lt;ISettingsPage, MyPage&gt;()</c>.
/// </summary>
public interface ISettingsPage
{
    /// <summary>Display name shown in the navigation list.</summary>
    string Title { get; }

    /// <summary>Emoji or icon character displayed next to the title.</summary>
    string Icon { get; }

    /// <summary>Optional category header for grouping pages (e.g. "Canvas", "Export").</summary>
    string? Category { get; }

    /// <summary>The ViewModel rendered inside the content area when this page is selected.</summary>
    object ViewModel { get; }

    /// <summary>
    /// Invoked by the Settings window whenever this page becomes the selected page.
    /// Default: no-op. Override to refresh page data on navigation.
    /// </summary>
    void OnSelected() { }
}
