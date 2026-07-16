using System;
using System.Globalization;
using System.Linq;
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

    public string Title => Entry.PdkName;

    public bool IsDeletedPdk => Entry.Kind == PdkTrashKind.DeletedPdk;

    public string KindLabel => IsDeletedPdk ? "PDK" : "Component(s)";

    public string Detail
    {
        get
        {
            var count = Entry.RestorableComponentNames.Count;
            if (IsDeletedPdk)
                return count == 1 ? "1 component" : $"{count} components";

            var names = string.Join(", ", Entry.RestorableComponentNames.Take(4));
            if (count > 4)
                names += $" +{count - 4}";
            return names;
        }
    }

    public string DeletedAtText => Entry.DeletedAt == DateTime.MinValue
        ? ""
        : Entry.DeletedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    [RelayCommand]
    private void Restore() => _owner.RestoreEntry(this);
}
