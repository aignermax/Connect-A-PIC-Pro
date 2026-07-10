using System;
using System.Linq;
using System.Threading.Tasks;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.Services.Solvers;
using CAP_Core.Solvers.Fdtd;
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

    /// <summary>Save is only possible once a matching preview has been rendered and no work is in flight.</summary>
    private bool CanSave => HasPreview && !IsBusy;

    partial void OnHasPreviewChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Recomputes the S-matrix from the rendered geometry via the FDTD solver. Any failure —
    /// no solver configured, an unavailable backend, or a failed solve — clears the pending
    /// model and reports the reason via <see cref="NewComponentViewModel.StatusText"/>; a
    /// black-box save is the only fallback, never a fabricated matrix.
    /// </summary>
    [RelayCommand]
    private async Task ComputeSMatrix()
    {
        if (IsBusy) return;
        if (_lastPreview is not { Success: true } preview || SelectedProcess is null)
        {
            StatusText = "Render a preview and select a process before computing the S-matrix.";
            return;
        }
        if (_fdtd is null)
        {
            StatusText = "FDTD solver is not configured.";
            return;
        }

        IsBusy = true;
        try
        {
            var availability = await _fdtd.CheckAvailabilityAsync();
            if (!availability.IsAvailable)
            {
                _computedModel = null;
                StatusText = availability.Message;
                return;
            }

            var portNames = preview.Pins.Select(p => p.Name).ToList();
            var request = ComponentFdtdRequestFactory.BuildFromPreview(preview.Raw, portNames);
            var result = await _fdtd.SolveAsync(request);
            if (!result.Success)
            {
                _computedModel = null;
                StatusText = result.Error ?? "FDTD solve failed.";
                return;
            }

            _computedModel = FdtdSMatrixConverter.ToComponentSMatrixData(result, "FDTD Meep");
            StatusText = "S-matrix computed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Saves the current component as a PDK component draft into the selected process's user
    /// PDK. Requires a name, a rendered preview, and a selected process — missing any of these
    /// reports why via <see cref="NewComponentViewModel.StatusText"/> and leaves
    /// <see cref="NewComponentViewModel.SavedDraft"/> null. A name collision is reported unless
    /// <see cref="NewComponentViewModel.ConfirmOverwrite"/> confirms it. The S-matrix is either
    /// the last FDTD result or a black box when none was computed — never fabricated. In
    /// own-code mode the draft's raw code/backend are set instead of a module/function
    /// reference. A black-box save preserves any pending diagnostic and prefixes it with a save
    /// confirmation.
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
        if (_lastPreview is not { Success: true } preview)
        {
            StatusText = "Render a preview before saving.";
            return;
        }
        if (SelectedProcess is null)
        {
            StatusText = "Select a fabrication process before saving.";
            return;
        }

        IsBusy = true;
        try
        {
            var process = SelectedProcess;
            if (_store.ComponentExists(process, name))
            {
                if (ConfirmOverwrite is null)
                {
                    StatusText = $"'{name}' already exists in {process.Name}.";
                    return;
                }
                if (!await ConfirmOverwrite(name, process.Name))
                {
                    StatusText = "Save cancelled.";
                    return;
                }
            }

            var reference = BuildReference();
            var sMatrix = _computedModel is null
                ? FdtdSMatrixToDraftConverter.BlackBox()
                : FdtdSMatrixToDraftConverter.FromFdtd(_computedModel);
            var backend = SelectedBackend == GeometryBackend.GdsFactory ? "gdsfactory" : "nazca";
            var draft = InputMode == NewComponentInputMode.OwnCode
                ? CustomComponentDraftFactory.Build(name, reference, preview, sMatrix, Code, backend)
                : CustomComponentDraftFactory.Build(name, reference, preview, sMatrix);

            _store.Save(process, draft, backend, null);

            SavedDraft = draft;
            SavedProcessName = process.Name;
            StatusText = _computedModel is null
                ? $"Saved as black box. {StatusText}".Trim()
                : "Saved with FDTD S-matrix.";
            Saved?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
