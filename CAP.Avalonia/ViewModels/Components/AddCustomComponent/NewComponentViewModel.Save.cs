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

/// <summary>
/// Save + FDTD-recompute path for <see cref="NewComponentViewModel"/> (split out purely to keep
/// each file under the project's line-count limit; still one partial class, one responsibility).
/// </summary>
public partial class NewComponentViewModel
{
    private ComponentSMatrixData? _computedModel;

    /// <summary>
    /// Save no longer requires a prior explicit preview — it renders/validates on its own via
    /// <see cref="NewComponentViewModel.EnsurePreviewAsync"/> — so this only requires no work in
    /// flight and a selected target PDK.
    /// </summary>
    private bool CanSave => !IsBusy && SelectedCustomPdk is not null;

    partial void OnIsBusyChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Recomputes the S-matrix from the rendered geometry via the FDTD solver, showing live
    /// progress (Meep lines + an elapsed-time heartbeat) via
    /// <see cref="NewComponentViewModel.StatusText"/> while it runs and cancellable via
    /// <see cref="NewComponentViewModel.CancelCompute"/>. Any failure — no solver configured,
    /// an unavailable backend, a failed solve, or a cancel — clears the pending model and
    /// reports the reason (raw, never guessed) via <c>StatusText</c>; a black-box save is the
    /// only fallback, never a fabricated matrix.
    /// </summary>
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
        }
    }

    /// <summary>
    /// Saves the current component as a PDK component draft, appended to the selected existing
    /// named custom PDK (<see cref="NewComponentViewModel.SelectedCustomPdk"/>) — a brand-new
    /// PDK is never created here, only via the <see cref="NewComponentViewModel.CreateNewPdk"/>
    /// modal hook, so by the time <c>Save</c> runs the target file already exists. Requires a
    /// name and a selected PDK — missing either reports why via
    /// <see cref="NewComponentViewModel.StatusText"/> and leaves
    /// <see cref="NewComponentViewModel.SavedDraft"/> null. A prior explicit Preview click is
    /// NOT required: Save renders/validates the current code itself via
    /// <see cref="NewComponentViewModel.EnsurePreviewAsync"/>, reusing an already-rendered,
    /// still-valid preview verbatim. A name collision is reported unless
    /// <see cref="NewComponentViewModel.ConfirmOverwrite"/> confirms it — except for a
    /// self-overwrite in <see cref="NewComponentViewModel.IsEditMode"/> (re-saving the edited
    /// component under its own original name), which is the intended save and skips the prompt. A
    /// rename onto a <em>different</em> existing component still collides and still prompts, so it
    /// is never silently clobbered. The S-matrix is either
    /// the last FDTD result or a black box when none was computed — never fabricated. The
    /// draft's source is always the user's own code (raw code + backend), never a
    /// module/function reference. A black-box save preserves any pending diagnostic and
    /// prefixes it with a save confirmation.
    /// </summary>
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

        IsBusy = true;
        try
        {
            // Renders/validates the current code itself when no (still-valid) preview exists —
            // a prior explicit Preview click is no longer a prerequisite. A render failure (e.g.
            // a Python syntax error) is reported via StatusText by EnsurePreviewAsync itself and
            // aborts the save; nothing is ever persisted from a failed or stale render.
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

            // A self-overwrite (re-saving the edited component under its own name) is exactly the
            // intended edit and needs no prompt. A rename onto a DIFFERENT existing component is a
            // real collision and must still go through ConfirmOverwrite — AppendToExistingPdk
            // removes-by-name, so skipping it would silently clobber the other component.
            var isSelfEdit = IsEditMode &&
                string.Equals(name, _editingOriginalName, StringComparison.OrdinalIgnoreCase);
            if (!isSelfEdit && _store.ComponentExistsInFile(pdk.FilePath, name) && !await ConfirmCollision(name, pdk.Name))
            {
                return;
            }
            SavedFilePath = _store.AppendToExistingPdk(pdk.FilePath, draft);

            SavedDraft = draft;
            StatusText = _computedModel is null
                ? $"Saved without simulation model (black box). {StatusText}".Trim()
                : "Saved with FDTD S-matrix.";
            Saved?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Reports and resolves a name collision via <see cref="NewComponentViewModel.ConfirmOverwrite"/>:
    /// true proceeds with the overwrite, false (or no confirmation hook) aborts with a status message.
    /// </summary>
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
