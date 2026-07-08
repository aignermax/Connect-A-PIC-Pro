using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// ViewModel for the application Settings window.
/// Enumerates all registered <see cref="ISettingsPage"/> implementations
/// and tracks which page is currently selected.
/// Adding a new settings page requires only a new class + one DI registration.
/// </summary>
public partial class SettingsWindowViewModel : ObservableObject
{
    /// <summary>All settings pages, ordered as registered in DI.</summary>
    public IReadOnlyList<ISettingsPage> Pages { get; }

    /// <summary>The currently selected settings page.</summary>
    [ObservableProperty]
    private ISettingsPage? _selectedPage;

    partial void OnSelectedPageChanged(ISettingsPage? value) => value?.OnSelected();

    /// <summary>
    /// Initializes a new instance of <see cref="SettingsWindowViewModel"/>.
    /// </summary>
    /// <param name="pages">All registered settings page implementations (injected by DI).</param>
    public SettingsWindowViewModel(IEnumerable<ISettingsPage> pages)
    {
        Pages = pages.ToList();
        SelectedPage = Pages.FirstOrDefault();
    }

    /// <summary>
    /// Selects the page whose runtime type matches <paramref name="pageType"/>;
    /// keeps the current selection when no such page is registered.
    /// </summary>
    /// <param name="pageType">Runtime type of the <see cref="ISettingsPage"/> to select.</param>
    public void SelectPage(Type pageType)
    {
        var page = Pages.FirstOrDefault(p => p.GetType() == pageType);
        if (page != null)
            SelectedPage = page;
    }
}
