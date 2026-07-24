using System.ComponentModel;
using CAP.Avalonia.Services.Localization;

namespace CAP.Avalonia.ViewModels.Settings;

/// <summary>
/// Base for settings pages whose navigation <see cref="Title"/> is a localized
/// string. Re-reads the title and raises <see cref="PropertyChanged"/> when the
/// UI language changes, so the Settings nav list updates live without reopening.
/// </summary>
public abstract class LocalizedSettingsPage : ISettingsPage, INotifyPropertyChanged, IDisposable
{
    private readonly string _titleKey;
    private readonly LocalizationService _localization;

    /// <summary>Initializes the page with its title key and the localization service.</summary>
    protected LocalizedSettingsPage(string titleKey, LocalizationService localization)
    {
        _titleKey = titleKey;
        _localization = localization;
        _localization.PropertyChanged += OnLocalizationChanged;
    }

    /// <inheritdoc/>
    public string Title => _localization.Translate(_titleKey);

    /// <inheritdoc/>
    public abstract string Icon { get; }

    /// <inheritdoc/>
    public virtual string? Category => null;

    /// <inheritdoc/>
    public abstract object ViewModel { get; }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc/>
    public virtual void OnSelected() { }

    /// <summary>Stops listening for language changes (called when the Settings window closes).</summary>
    public void Dispose() => _localization.PropertyChanged -= OnLocalizationChanged;

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
}
