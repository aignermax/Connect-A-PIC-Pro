using System;
using System.Collections.ObjectModel;
using CAP_Core;
using CAP_DataAccess.Components.AddCustomComponent;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Panels.PdkTrash;

public partial class PdkTrashViewModel : ObservableObject
{
    private readonly PdkTrashService _trash;
    private readonly ErrorConsoleService? _errorConsole;

    public ObservableCollection<PdkTrashEntryViewModel> Entries { get; } = new();

    [ObservableProperty] private bool _isOpen;

    [ObservableProperty] private string _statusText = "";

    public bool HasEntries => Entries.Count > 0;

    public Action<PdkTrashRestoreResult>? OnRestored { get; set; }

    public PdkTrashViewModel(PdkTrashService trash, ErrorConsoleService? errorConsole = null)
    {
        _trash = trash ?? throw new ArgumentNullException(nameof(trash));
        _errorConsole = errorConsole;
    }

    public void Refresh()
    {
        Entries.Clear();
        foreach (var entry in _trash.ListEntries())
            Entries.Add(new PdkTrashEntryViewModel(entry, this));
        OnPropertyChanged(nameof(HasEntries));
    }

    [RelayCommand]
    private void Open()
    {
        Refresh();
        StatusText = HasEntries ? "" : "Trash is empty.";
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    public void RestoreEntry(PdkTrashEntryViewModel? item)
    {
        if (item is null) return;
        try
        {
            var result = _trash.Restore(item.Entry);
            OnRestored?.Invoke(result);
            StatusText = result.Kind == PdkTrashKind.DeletedPdk
                ? $"Restored '{result.PdkName}'."
                : $"Restored {result.RestoredComponents.Count} component(s) to '{result.PdkName}'.";
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Restore from trash failed: {ex.Message}", ex);
            StatusText = $"Restore failed: {ex.Message}";
        }
        Refresh();
    }

}
