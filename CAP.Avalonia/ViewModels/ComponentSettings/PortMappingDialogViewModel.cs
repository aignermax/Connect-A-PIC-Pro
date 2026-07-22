using System.Collections.ObjectModel;
using CAP.Avalonia.Services.Localization;
using CAP_DataAccess.Import;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.ComponentSettings;

/// <summary>
/// ViewModel backing the port-mapping dialog. Built when an imported S-parameter
/// file's port names ("port 1", "port 2", …) don't match the component's pin
/// names ("in", "out1", …), so the user can disambiguate the assignment before
/// the override is stored.
///
/// Defaults follow <see cref="PortNameMapping.BuildDefaultMapping"/>: imported
/// names that already match a component pin keep their target; the rest fall
/// back to positional pairing. The user can re-pick any row.
/// </summary>
public partial class PortMappingDialogViewModel : ObservableObject
{
    /// <summary>Title shown in the window header.</summary>
    [ObservableProperty]
    private string _title = LocalizationService.Instance.Translate("PortMap.DefaultTitle");

    /// <summary>One-line summary of why the dialog appeared, surfaced above the rows.</summary>
    [ObservableProperty]
    private string _explanation = "";

    /// <summary>Per-imported-port editable rows.</summary>
    public ObservableCollection<PortMappingRowViewModel> Rows { get; } = new();

    /// <summary>Pin names the dropdowns are populated with — shared across rows.</summary>
    public ObservableCollection<string> AvailablePins { get; } = new();

    /// <summary>
    /// Configures the dialog with the imported names and the component's pin names.
    /// Builds default mappings via <see cref="PortNameMapping.BuildDefaultMapping"/>;
    /// the user can edit each row before confirming.
    /// </summary>
    public void Configure(
        IReadOnlyList<string> importedNames,
        IReadOnlyList<string> componentPinNames,
        string componentDisplayName)
    {
        AvailablePins.Clear();
        foreach (var name in componentPinNames)
            AvailablePins.Add(name);

        Title = string.Format(LocalizationService.Instance.Translate("PortMap.Title"), componentDisplayName);
        Explanation = LocalizationService.Instance.Translate("PortMap.Explanation");

        var defaults = PortNameMapping.BuildDefaultMapping(importedNames, componentPinNames);

        Rows.Clear();
        foreach (var imported in importedNames)
        {
            Rows.Add(new PortMappingRowViewModel(
                imported,
                AvailablePins,
                defaults[imported]));
        }
    }

    /// <summary>
    /// Snapshots the user's selections as an imported→component mapping suitable
    /// for <see cref="PortNameMapping.Remap"/>. Returns null when the user has
    /// duplicated a target pin across rows — the caller surfaces that as an
    /// error rather than letting two ports collapse silently into one.
    /// </summary>
    public IReadOnlyDictionary<string, string>? BuildResultOrNull(out string? errorReason)
    {
        var mapping = new Dictionary<string, string>(Rows.Count, StringComparer.Ordinal);
        var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in Rows)
        {
            if (string.IsNullOrEmpty(row.SelectedPin))
            {
                errorReason = string.Format(
                    LocalizationService.Instance.Translate("PortMap.NoTargetPin"), row.ImportedName);
                return null;
            }
            if (!usedTargets.Add(row.SelectedPin))
            {
                errorReason = string.Format(
                    LocalizationService.Instance.Translate("PortMap.DuplicateTarget"), row.SelectedPin);
                return null;
            }
            mapping[row.ImportedName] = row.SelectedPin;
        }

        errorReason = null;
        return mapping;
    }
}
