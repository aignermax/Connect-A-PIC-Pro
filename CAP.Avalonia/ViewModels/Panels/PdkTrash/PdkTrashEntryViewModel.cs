using System;
using System.Globalization;
using CAP_DataAccess.Components.AddCustomComponent;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Panels.PdkTrash;

public partial class PdkTrashEntryViewModel : ObservableObject
{
    private readonly PdkTrashViewModel _owner;

    public PdkTrashEntry Entry { get; }

    public PdkTrashEntryViewModel(PdkTrashEntry entry, PdkTrashViewModel owner)
    {
        Entry = entry;
        _owner = owner;
    }

    public bool IsDeletedPdk => Entry.Kind == PdkTrashKind.DeletedPdk;

    public string KindLabel => IsDeletedPdk ? "PDK" : "Component";

    public string Title => IsDeletedPdk ? Entry.PdkName : Entry.RestorableComponentNames[0];

    public string Detail
    {
        get
        {
            if (IsDeletedPdk)
            {
                var count = Entry.RestorableComponentNames.Count;
                return count == 1 ? "1 component" : $"{count} components";
            }

            return Entry.PdkName;
        }
    }

    public string DeletedAtText => Entry.DeletedAt == DateTime.MinValue
        ? ""
        : Entry.DeletedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    [RelayCommand]
    private void Restore() => _owner.RestoreEntry(this);
}
