using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.Services.Solvers;
using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Persistence.PIR;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Components.AddCustomComponent;

public partial class NewComponentViewModel
{
    private ComponentSMatrixData? _computedModel;

    private bool CanSave => !IsBusy && SelectedCustomPdk is not null;

    partial void OnIsBusyChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private async Task ComputeSMatrix()
    {
        if (IsBusy) return;
        if (_lastPreview is not { Success: true } preview || SelectedProcess is null)
        {
            StatusText = "Render a preview and select a PDK before computing the S-matrix.";
            return;
        }
        if (_fdtd is null)
        {
            StatusText = "FDTD solver is not configured.";
            return;
        }

        IsBusy = true;
        _computeCts = new CancellationTokenSource();
        try
        {
            var availability = await _fdtd.CheckAvailabilityAsync(_computeCts.Token);
            if (!availability.IsAvailable)
            {
                _computedModel = null;
                StatusText = availability.Message;
                return;
            }

            var portNames = preview.Pins.Select(p => p.Name).ToList();
            var request = ComponentFdtdRequestFactory.BuildFromPreview(preview.Raw, portNames);
            var result = await RunSolveWithLiveStatusAsync(request, _computeCts.Token);
            if (!result.Success)
            {
                _computedModel = null;
                StatusText = result.Error ?? "FDTD solve failed.";
                return;
            }

            _computedModel = FdtdSMatrixConverter.ToComponentSMatrixData(result, "FDTD Meep");
            StatusText = $"S-matrix computed ({result.Wavelengths.Count} wavelength(s)).";
        }
        catch (OperationCanceledException)
        {
            _computedModel = null;
            StatusText = "S-matrix computation cancelled.";
        }
        finally
        {
            IsBusy = false;
            _computeCts?.Dispose();
            _computeCts = null;
            RefreshSMatrixEntries();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        if (IsBusy) return;
        var name = ComponentName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusText = "Enter a component name before saving.";
            return;
        }
        var pdk = SelectedCustomPdk;
        if (pdk is null)
        {
            StatusText = "Select a PDK before saving.";
            return;
        }

        MigratedFromPdkName = null;
        // While a bundled fork is pending, the "original" is the read-only built-in PDK — a
        // save into a different PDK is a copy-out, never a migration that would remove the
        // bundled component from the library.
        var isMigration = IsEditMode
            && !HasPendingBundledFork
            && _editOriginalPdkFilePath is not null
            && !PathsEqual(_editOriginalPdkFilePath, pdk.FilePath);
        if (isMigration &&
            !string.Equals(_editOriginalProcessName, SelectedProcess?.Name, StringComparison.OrdinalIgnoreCase))
        {
            StatusText = $"Cannot move '{name}' to PDK '{pdk.Name}': it uses a different fabrication " +
                         $"process ('{SelectedProcess?.Name}' vs '{_editOriginalProcessName}').";
            return;
        }

        IsBusy = true;
        try
        {
            if (!await EnsurePreviewAsync() || _lastPreview is not { Success: true } preview)
            {
                return;
            }

            var reference = BuildReference();
            var sMatrix = _computedModel is null
                ? FdtdSMatrixToDraftConverter.BlackBox()
                : FdtdSMatrixToDraftConverter.FromFdtd(_computedModel);
            var backend = SelectedBackend == GeometryBackend.GdsFactory ? "gdsfactory" : "nazca";
            var draft = CustomComponentDraftFactory.Build(name, reference, preview, sMatrix, Code, backend);

            var isSelfEdit = IsEditMode && !isMigration &&
                string.Equals(name, _editingOriginalName, StringComparison.OrdinalIgnoreCase);
            // The deferred fork-on-save: the target file does not exist until now, so probe
            // name collisions against the bundled source the fork will be copied from.
            var executesPendingFork = HasPendingBundledFork
                && _pendingForkTargetPath is not null
                && PathsEqual(pdk.FilePath, _pendingForkTargetPath);
            var collisionProbePath = executesPendingFork ? _pendingForkSourcePath! : pdk.FilePath;
            if (!isSelfEdit && _store.ComponentExistsInFile(collisionProbePath, name) && !await ConfirmCollision(name, pdk.Name))
            {
                return;
            }
            if (executesPendingFork)
            {
                _store.ForkBundledPdk(_pendingForkSourcePath!, pdk.Name);
            }
            SavedFilePath = _store.AppendToExistingPdk(pdk.FilePath, draft);
            SavedDraft = draft;
            // Only a save that actually executed the deferred fork may shadow the bundled
            // PDK in the library — a mere name match must not (PR #742 review, finding 1).
            SavedViaPendingBundledFork = executesPendingFork;
            if (executesPendingFork)
            {
                // From here on this session edits the user's copy directly.
                _pendingForkSourcePath = null;
                _pendingForkTargetPath = null;
            }

            if (isMigration && TryRemoveFromOriginalPdk(_editingOriginalName ?? name))
            {
                MigratedFromPdkName = _editOriginalPdkName;
                MigratedFromComponentName = _editingOriginalName ?? name;
                _editOriginalPdkFilePath = pdk.FilePath;
                _editOriginalPdkName = pdk.Name;
                _editOriginalProcessName = SelectedProcess?.Name;
                _editingOriginalName = name;
            }

            // A migration whose removal threw already set an explanatory StatusText (the component
            // now lives in both PDKs) — don't overwrite it with a plain save-success message.
            if (!isMigration || MigratedFromPdkName != null)
            {
                StatusText = MigratedFromPdkName != null
                    ? $"Moved '{name}' to PDK '{pdk.Name}'."
                    : _computedModel is null
                        ? $"Saved without simulation model (black box). {StatusText}".Trim()
                        : "Saved with FDTD S-matrix.";
            }
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // Without a catch, the AsyncRelayCommand swallows the fault and the Save button
            // looks like it silently did nothing (PR #742 review, finding 2) — e.g. when the
            // target fork file was reverted to trash underneath an open editor.
            StatusText = $"Save failed: {ex.Message}";
            _errorConsole?.LogError($"Saving component '{name}' to PDK '{pdk.Name}' failed: {ex.Message}", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool PathsEqual(string? a, string? b) =>
        a != null && b != null &&
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private bool TryRemoveFromOriginalPdk(string name)
    {
        try
        {
            _store.RemoveComponent(_editOriginalPdkFilePath!, name);
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Saved to '{SelectedCustomPdk?.Name}', but could not remove the original " +
                         $"copy from '{_editOriginalPdkName}': {ex.Message}";
            return false;
        }
    }

    private async Task<bool> ConfirmCollision(string componentName, string targetName)
    {
        if (ConfirmOverwrite is null)
        {
            StatusText = $"'{componentName}' already exists in '{targetName}'.";
            return false;
        }
        if (!await ConfirmOverwrite(componentName, targetName))
        {
            StatusText = "Save cancelled.";
            return false;
        }
        return true;
    }
}
