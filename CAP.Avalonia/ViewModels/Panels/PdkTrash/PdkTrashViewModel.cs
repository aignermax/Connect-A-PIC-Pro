using System;
using System.Collections.ObjectModel;
using CAP_Core;
using CAP_DataAccess.Components.AddCustomComponent;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Panels.PdkTrash;

/// <summary>
/// Drives the PDK-Management trash flyout: lists recoverable deleted PDKs and removed-component
/// backups (<see cref="PdkTrashService"/>) and restores or permanently purges them. Restoring
/// only touches files on disk here; re-registering the result into the running library is the
/// host's job via <see cref="OnRestored"/> (wired to the same RegisterCreatedPdk /
/// RegisterSavedCustomComponent hooks the delete path is the mirror of).
/// </summary>
public partial class PdkTrashViewModel : ObservableObject
{
    private readonly PdkTrashService _trash;
    private readonly ErrorConsoleService? _errorConsole;

    /// <summary>Recoverable trash entries, newest first.</summary>
    public ObservableCollection<PdkTrashEntryViewModel> Entries { get; } = new();

    /// <summary>True while the flyout is open.</summary>
    [ObservableProperty] private bool _isOpen;

    /// <summary>Status / error line shown at the bottom of the flyout.</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>True when there is at least one recoverable item (drives the empty state).</summary>
    public bool HasEntries => Entries.Count > 0;

    /// <summary>
    /// Invoked after a successful restore so the host can re-register the restored PDK/components
    /// into the live library (the mirror of what the delete flow unregistered).
    /// </summary>
    public Action<PdkTrashRestoreResult>? OnRestored { get; set; }

    /// <summary>Initialises the trash ViewModel.</summary>
    public PdkTrashViewModel(PdkTrashService trash, ErrorConsoleService? errorConsole = null)
    {
        _trash = trash ?? throw new ArgumentNullException(nameof(trash));
        _errorConsole = errorConsole;
    }

    /// <summary>Re-reads the trash folder into <see cref="Entries"/>.</summary>
    public void Refresh()
    {
        Entries.Clear();
        foreach (var entry in _trash.ListEntries())
            Entries.Add(new PdkTrashEntryViewModel(entry, this));
        OnPropertyChanged(nameof(HasEntries));
    }

    /// <summary>Opens the flyout, refreshing its contents first.</summary>
    [RelayCommand]
    private void Open()
    {
        Refresh();
        StatusText = HasEntries ? "" : "Papierkorb ist leer.";
        IsOpen = true;
    }

    /// <summary>Closes the flyout.</summary>
    [RelayCommand]
    private void Close() => IsOpen = false;

    /// <summary>Restores a trashed PDK / removed components and re-registers them into the library.</summary>
    public void RestoreEntry(PdkTrashEntryViewModel? item)
    {
        if (item is null) return;
        try
        {
            var result = _trash.Restore(item.Entry);
            OnRestored?.Invoke(result);
            StatusText = result.Kind == PdkTrashKind.DeletedPdk
                ? $"'{result.PdkName}' wiederhergestellt."
                : $"{result.RestoredComponents.Count} Komponente(n) in '{result.PdkName}' wiederhergestellt.";
        }
        catch (Exception ex)
        {
            // A locked/corrupt file must surface, not crash the click handler (CLAUDE.md §silent-failure).
            _errorConsole?.LogError($"Wiederherstellen aus Papierkorb fehlgeschlagen: {ex.Message}", ex);
            StatusText = $"Fehler beim Wiederherstellen: {ex.Message}";
        }
        Refresh();
    }

    /// <summary>Permanently deletes a trash entry (irreversible), after which it is gone for good.</summary>
    public void PurgeEntry(PdkTrashEntryViewModel? item)
    {
        if (item is null) return;
        try
        {
            _trash.Purge(item.Entry);
            StatusText = $"'{item.Title}' endgültig gelöscht.";
        }
        catch (Exception ex)
        {
            _errorConsole?.LogError($"Endgültiges Löschen fehlgeschlagen: {ex.Message}", ex);
            StatusText = $"Fehler: {ex.Message}";
        }
        Refresh();
    }
}
