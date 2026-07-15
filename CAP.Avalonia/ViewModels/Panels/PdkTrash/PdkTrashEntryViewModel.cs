using System;
using System.Globalization;
using System.Linq;
using CAP_DataAccess.Components.AddCustomComponent;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Panels.PdkTrash;

/// <summary>
/// Display wrapper for one <see cref="PdkTrashEntry"/> in the PDK trash flyout: turns the raw
/// entry into human-readable strings and carries the Restore / permanently-delete commands (which
/// delegate to the owning <see cref="PdkTrashViewModel"/>), so the item template can bind them
/// directly without an ancestor cast.
/// </summary>
public partial class PdkTrashEntryViewModel
{
    private readonly PdkTrashViewModel _owner;

    /// <summary>The underlying trash entry (passed back to the service on restore/purge).</summary>
    public PdkTrashEntry Entry { get; }

    /// <summary>Creates the display wrapper bound to its owning trash ViewModel.</summary>
    public PdkTrashEntryViewModel(PdkTrashEntry entry, PdkTrashViewModel owner)
    {
        Entry = entry;
        _owner = owner;
    }

    /// <summary>Primary line, e.g. the PDK name.</summary>
    public string Title => Entry.PdkName;

    /// <summary>True for a whole deleted PDK (drives the icon/label in the flyout).</summary>
    public bool IsDeletedPdk => Entry.Kind == PdkTrashKind.DeletedPdk;

    /// <summary>Short kind label shown as a chip.</summary>
    public string KindLabel => IsDeletedPdk ? "PDK" : "Komponente(n)";

    /// <summary>
    /// Secondary line: what exactly would be restored — the whole PDK with its component count,
    /// or the specific removed component names.
    /// </summary>
    public string Detail
    {
        get
        {
            var count = Entry.RestorableComponentNames.Count;
            if (IsDeletedPdk)
                return count == 1 ? "1 Komponente" : $"{count} Komponenten";

            var names = string.Join(", ", Entry.RestorableComponentNames.Take(4));
            if (count > 4)
                names += $" +{count - 4}";
            return names;
        }
    }

    /// <summary>When the item was deleted, formatted for display.</summary>
    public string DeletedAtText => Entry.DeletedAt == DateTime.MinValue
        ? ""
        : Entry.DeletedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Restores this entry (whole PDK or removed components).</summary>
    [RelayCommand]
    private void Restore() => _owner.RestoreEntry(this);

    /// <summary>Permanently removes this entry from the trash (irreversible).</summary>
    [RelayCommand]
    private void Purge() => _owner.PurgeEntry(this);
}
