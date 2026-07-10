using System;
using System.Collections.Generic;
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
/// component as a black box (no S-matrix), never a fabricated one. The save/FDTD-compute path
/// lives in the <c>NewComponentViewModel.Save.cs</c> partial (kept a separate file for size).
/// </summary>
public partial class NewComponentViewModel : ObservableObject
{
    private readonly ComponentGeometryExtractor _extractor;
    private readonly IFdtdSMatrixService? _fdtd;
    private readonly UserPdkStore _store;

    private GeometryExtractResult? _lastPreview;

    [ObservableProperty] private string _componentName = string.Empty;
    [ObservableProperty] private GeometryBackend _selectedBackend = GeometryBackend.GdsFactory;
    [ObservableProperty] private string? _module;
    [ObservableProperty] private string _function = string.Empty;
    [ObservableProperty] private string? _parameters;
    [ObservableProperty] private ProcessDefinition? _selectedProcess;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private NewComponentInputMode _inputMode = NewComponentInputMode.Reference;
    [ObservableProperty] private string _code = string.Empty;

    /// <summary>Fabrication processes available for the "save to" selection.</summary>
    public IReadOnlyList<ProcessDefinition> Processes { get; }

    /// <summary>
    /// Geometry backends selectable for <see cref="InputMode"/>: reference mode offers
    /// gdsfactory only (a nazca custom component needs a derived NazcaOriginOffset, v2);
    /// own-code mode offers both, since raw code exports via the per-instance override path.
    /// </summary>
    public IReadOnlyList<GeometryBackend> AvailableBackends =>
        InputMode == NewComponentInputMode.OwnCode
            ? new[] { GeometryBackend.GdsFactory, GeometryBackend.Nazca }
            : new[] { GeometryBackend.GdsFactory };

    /// <summary>
    /// File-picker hook for <see cref="LoadCodeFromFile"/>: returns a ".py" file's already-read
    /// contents, or null if cancelled. Null keeps the command a no-op — no direct file dialog.
    /// </summary>
    public Func<Task<string?>>? PickPyFile { get; set; }

    /// <summary>The draft last written by <c>Save</c>, or null before a successful save.</summary>
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
    // inputs. Clearing _lastPreview is the load-bearing part: Save gates on that field, so a
    // stale preview cannot be saved; HasPreview (Save button's enablement) tracks it.
    partial void OnSelectedBackendChanged(GeometryBackend value) => InvalidatePreview();
    partial void OnModuleChanged(string? value) => InvalidatePreview();
    partial void OnFunctionChanged(string value) => InvalidatePreview();
    partial void OnParametersChanged(string? value) => InvalidatePreview();
    partial void OnCodeChanged(string value) => InvalidatePreview();

    partial void OnInputModeChanged(NewComponentInputMode value)
    {
        OnPropertyChanged(nameof(AvailableBackends));
        InvalidatePreview();
    }

    private void InvalidatePreview()
    {
        _lastPreview = null;
        HasPreview = false;
    }

    /// <summary>Raw code in own-code mode, else the module/function/parameters reference.</summary>
    private GeometryReference BuildReference() =>
        InputMode == NewComponentInputMode.OwnCode
            ? GeometryReference.RawCode(SelectedBackend, Code)
            : new GeometryReference(SelectedBackend, Module, Function, Parameters);

    /// <summary>Loads Python source into <see cref="Code"/> via the injected <see cref="PickPyFile"/> hook.</summary>
    [RelayCommand]
    private async Task LoadCodeFromFile()
    {
        if (PickPyFile is null) return;
        var content = await PickPyFile();
        if (content is not null) Code = content;
    }

    /// <summary>Renders the configured geometry reference and extracts its size and pins.</summary>
    [RelayCommand]
    private async Task RunPreview()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var reference = BuildReference();
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
}
