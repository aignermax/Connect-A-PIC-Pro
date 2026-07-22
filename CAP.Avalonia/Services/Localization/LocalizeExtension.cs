using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace CAP.Avalonia.Services.Localization;

/// <summary>
/// AXAML markup extension that binds a control property to a localized string:
/// <c>Text="{loc:Localize Toolbar.ModeLabel}"</c>. The binding targets
/// <see cref="LocalizationService.Instance"/>'s indexer, so all texts update live
/// when the language is switched in Settings.
/// </summary>
public sealed class LocalizeExtension : MarkupExtension
{
    /// <summary>Creates the extension for the given string-table key.</summary>
    public LocalizeExtension(string key)
    {
        Key = key;
    }

    /// <summary>The string-table key (must exist in <c>Assets/i18n/strings-en.json</c>).</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; }

    /// <inheritdoc/>
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay,
        };
    }
}
