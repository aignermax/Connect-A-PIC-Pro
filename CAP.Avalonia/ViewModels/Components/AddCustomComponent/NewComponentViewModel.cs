using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.Services.Solvers;
using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Persistence.PIR;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Components.AddCustomComponent;

/// <summary>
/// Orchestrates adding a user-authored component to a fabrication process's user PDK:
/// renders its geometry (nazca or gdsfactory), optionally recomputes its S-matrix via the
/// FDTD solver, and saves the result as a <see cref="PdkComponentDraft"/>. Never invents
/// physics — a missing solver, an unavailable backend, or a failed solve always saves the
/// component as a black box (no S-matrix), never a fabricated one.
/// </summary>
public partial class NewComponentViewModel : ObservableObject
{
    private readonly ComponentGeometryExtractor _extractor;
    private readonly IFdtdSMatrixService? _fdtd;
    private readonly UserPdkStore _store;

    private GeometryExtractResult? _lastPreview;
    private ComponentSMatrixData? _computedModel;

    [ObservableProperty] private string _componentName = string.Empty;
    [ObservableProperty] private GeometryBackend _selectedBackend = GeometryBackend.GdsFactory;
    [ObservableProperty] private string? _module;
    [ObservableProperty] private string _function = string.Empty;
    [ObservableProperty] private string? _parameters;
    [ObservableProperty] private ProcessDefinition? _selectedProcess;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasPreview;

    /// <summary>Fabrication processes available for the "save to" selection.</summary>
    public IReadOnlyList<ProcessDefinition> Processes { get; }

    /// <summary>
    /// Geometry backends selectable in the UI. v1 offers gdsfactory only: a nazca custom
    /// component saved without a derived <c>NazcaOriginOffset</c> has no clean export/sim path,
    /// so nazca custom components are deferred to v2 (needs NazcaOriginOffset derivation). The
    /// <see cref="GeometryBackend"/> enum and the extractor's nazca branch stay for tests/v2.
    /// </summary>
    public static IReadOnlyList<GeometryBackend> AvailableBackends { get; } =
        new[] { GeometryBackend.GdsFactory };

    /// <summary>The draft last written by <see cref="Save"/>, or null before a successful save.</summary>
    public PdkComponentDraft? SavedDraft { get; private set; }

    /// <summary>The process name <see cref="SavedDraft"/> was saved under.</summary>
    public string? SavedProcessName { get; private set; }

    /// <summary>Raised after a successful save, so a listener (e.g. the left panel) can refresh.</summary>
    public event EventHandler? Saved;

    /// <summary>
    /// Optional confirmation hook invoked with (componentName, processName) when a save would
    /// overwrite an existing component; returning true proceeds with the overwrite. When null,
    /// a collision is reported via <see cref="StatusText"/> and the save is aborted.
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmOverwrite { get; set; }

    /// <summary>Initializes the view model with its collaborators and the available processes.</summary>
    public NewComponentViewModel(
        ComponentGeometryExtractor extractor,
        IFdtdSMatrixService? fdtd,
        UserPdkStore store,
        IReadOnlyList<ProcessDefinition> processes)
    {
        _extractor = extractor;
        _fdtd = fdtd;
        _store = store;
        Processes = processes;
    }

    // A change to any input the preview was rendered from invalidates the preview — otherwise
    // a saved draft could be built from a rendered preview that no longer matches the current
    // Module/Function/Parameters/Backend (drift between the last render and what gets saved).
    // Clearing _lastPreview is the load-bearing part: Save gates on that field, so a stale
    // preview cannot be saved; HasPreview (which drives the Save button's enablement) tracks it.
    partial void OnSelectedBackendChanged(GeometryBackend value) => InvalidatePreview();
    partial void OnModuleChanged(string? value) => InvalidatePreview();
    partial void OnFunctionChanged(string value) => InvalidatePreview();
    partial void OnParametersChanged(string? value) => InvalidatePreview();

    private void InvalidatePreview()
    {
        _lastPreview = null;
        HasPreview = false;
    }

    /// <summary>Save is only possible once a matching preview has been rendered and no work is in flight.</summary>
    private bool CanSave => HasPreview && !IsBusy;

    partial void OnHasPreviewChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    /// <summary>Renders the configured geometry reference and extracts its size and pins.</summary>
    [RelayCommand]
    private async Task RunPreview()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var reference = new GeometryReference(SelectedBackend, Module, Function, Parameters);
            var result = await _extractor.ExtractAsync(reference);
            _lastPreview = result;
            HasPreview = result.Success;
            StatusText = result.Success
                ? $"Preview rendered: {result.WidthUm:0.###} x {result.HeightUm:0.###} um, {result.Pins.Count} pins."
                : result.Error ?? "Preview render failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Recomputes the S-matrix from the rendered geometry via the FDTD solver. Any failure —
    /// no solver configured, an unavailable backend, or a failed solve — clears the pending
    /// model and reports the reason via <see cref="StatusText"/>; a black-box save is the
    /// only fallback, never a fabricated matrix.
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
    /// Saves the current component as a <see cref="PdkComponentDraft"/> into the selected
    /// process's user PDK. Requires a name, a rendered preview, and a selected process —
    /// missing any of these reports why via <see cref="StatusText"/> and leaves
    /// <see cref="SavedDraft"/> null. A name collision is reported via <see cref="StatusText"/>
    /// unless <see cref="ConfirmOverwrite"/> confirms the overwrite. On success the S-matrix is
    /// either the last FDTD result or a black box when none was computed — never fabricated. A
    /// black-box save preserves any pending diagnostic in <see cref="StatusText"/> (e.g. an FDTD
    /// failure explaining why the save is a black box) and prefixes it with a save confirmation,
    /// so the user always gets confirmation without losing the reason there is no model.
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

            var reference = new GeometryReference(SelectedBackend, Module, Function, Parameters);
            var sMatrix = _computedModel is null
                ? FdtdSMatrixToDraftConverter.BlackBox()
                : FdtdSMatrixToDraftConverter.FromFdtd(_computedModel);
            var draft = CustomComponentDraftFactory.Build(name, reference, preview, sMatrix);

            var backend = SelectedBackend == GeometryBackend.GdsFactory ? "gdsfactory" : "nazca";
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
