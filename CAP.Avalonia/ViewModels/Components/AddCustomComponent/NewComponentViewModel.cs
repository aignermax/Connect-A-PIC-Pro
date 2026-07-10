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
/// Orchestrates adding a user-authored component to a PDK-first target: either an existing
/// named custom PDK (<see cref="SelectedCustomPdk"/>, process inherited) or a brand-new one
/// (<see cref="IsNewPdk"/>, name + process chosen). Renders the component's own Python code
/// (nazca or gdsfactory), optionally recomputes its S-matrix via the FDTD solver, and saves the
/// result as a <see cref="PdkComponentDraft"/>. Never invents physics — a missing solver, an
/// unavailable backend, or a failed solve always saves the component as a black box (no
/// S-matrix), never a fabricated one. The save/FDTD-compute path lives in the
/// <c>NewComponentViewModel.Save.cs</c> partial (kept a separate file for size).
/// </summary>
public partial class NewComponentViewModel : ObservableObject
{
    private static readonly IReadOnlyList<GeometryBackend> _availableBackends =
        new[] { GeometryBackend.GdsFactory, GeometryBackend.Nazca };

    private readonly ComponentGeometryExtractor _extractor;
    private readonly IFdtdSMatrixService? _fdtd;
    private readonly UserPdkStore _store;

    private GeometryExtractResult? _lastPreview;

    [ObservableProperty] private string _componentName = string.Empty;
    [ObservableProperty] private GeometryBackend _selectedBackend = GeometryBackend.GdsFactory;
    [ObservableProperty] private ProcessDefinition? _selectedProcess;
    [ObservableProperty] private UserPdkInfo? _selectedCustomPdk;
    [ObservableProperty] private bool _isNewPdk;
    [ObservableProperty] private string _newPdkName = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private string _code = string.Empty;

    /// <summary>Fabrication processes available for a new PDK's process selection.</summary>
    public IReadOnlyList<ProcessDefinition> Processes { get; }

    /// <summary>Named custom PDKs already on disk, offered as an alternative to creating a new one.</summary>
    public IReadOnlyList<UserPdkInfo> AvailableCustomPdks { get; }

    /// <summary>Geometry backends selectable for the rendered code: always both, since saving is always own-code.</summary>
    public IReadOnlyList<GeometryBackend> AvailableBackends => _availableBackends;

    /// <summary>
    /// The process the component will be saved under: <see cref="SelectedProcess"/> for a new
    /// PDK, or the selected existing custom PDK's (inherited, read-only) process otherwise.
    /// </summary>
    private ProcessDefinition? EffectiveProcess => IsNewPdk ? SelectedProcess : SelectedCustomPdk?.Process;

    /// <summary>
    /// File-picker hook for <see cref="LoadCodeFromFile"/>: returns a ".py" file's already-read
    /// contents, or null if cancelled. Null keeps the command a no-op — no direct file dialog.
    /// </summary>
    public Func<Task<string?>>? PickPyFile { get; set; }

    /// <summary>
    /// Hook invoked by <see cref="OpenProcessEditorCmd"/> to open a fabrication-process editor
    /// (e.g. from the "New PDK" section). A no-op when null.
    /// </summary>
    public Func<Task>? OpenProcessEditor { get; set; }

    /// <summary>The draft last written by <c>Save</c>, or null before a successful save.</summary>
    public PdkComponentDraft? SavedDraft { get; private set; }

    /// <summary>
    /// The user-PDK file path <c>Save</c> actually wrote to (named custom PDK, new or
    /// existing) — never derived from <see cref="SelectedProcess"/>, since a process's default
    /// per-process file is no longer where a PDK-first save lands. Null before a successful save.
    /// </summary>
    public string? SavedFilePath { get; private set; }

    /// <summary>Raised after a successful save, so a listener (e.g. the left panel) can refresh.</summary>
    public event EventHandler? Saved;

    /// <summary>
    /// Optional confirmation hook invoked with (componentName, pdkName) when a save would
    /// overwrite an existing component or PDK file; returning true proceeds with the overwrite.
    /// When null, a collision is reported via <see cref="StatusText"/> and the save is aborted.
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmOverwrite { get; set; }

    /// <summary>
    /// Initializes the view model with its collaborators, the available processes (for a new
    /// PDK), and the already-existing named custom PDKs read from <paramref name="store"/>.
    /// Defaults to "new PDK" mode when no custom PDK exists yet.
    /// </summary>
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
        AvailableCustomPdks = store.ListCustomPdks();

        // Pre-select the first existing custom PDK rather than leaving the transient
        // "one exists but none chosen" state (SelectedCustomPdk null, IsNewPdk false)
        // hanging around; falls back to "new PDK" only when none exist yet.
        if (AvailableCustomPdks.Count > 0)
        {
            SelectedCustomPdk = AvailableCustomPdks[0];
        }
        else
        {
            IsNewPdk = true;
        }
    }

    // A change to any input the preview was rendered from invalidates the preview — otherwise
    // a saved draft could be built from a rendered preview that no longer matches the current
    // inputs. Clearing _lastPreview is the load-bearing part: Save gates on that field, so a
    // stale preview cannot be saved; HasPreview (Save button's enablement) tracks it.
    partial void OnSelectedBackendChanged(GeometryBackend value) => InvalidatePreview();
    partial void OnCodeChanged(string value) => InvalidatePreview();

    /// <summary>Switching the custom-PDK selection re-derives <see cref="IsNewPdk"/> and inherits its process.</summary>
    partial void OnSelectedCustomPdkChanged(UserPdkInfo? value)
    {
        IsNewPdk = value is null;
        SelectedProcess = value?.Process;
        InvalidatePreview();
        SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsNewPdkChanged(bool value) => InvalidatePreview();

    private void InvalidatePreview()
    {
        _lastPreview = null;
        HasPreview = false;
    }

    /// <summary>The rendered geometry reference: always the user's own code, verbatim.</summary>
    private GeometryReference BuildReference() => GeometryReference.RawCode(SelectedBackend, Code);

    /// <summary>Loads Python source into <see cref="Code"/> via the injected <see cref="PickPyFile"/> hook.</summary>
    [RelayCommand]
    private async Task LoadCodeFromFile()
    {
        if (PickPyFile is null) return;
        var content = await PickPyFile();
        if (content is not null) Code = content;
    }

    /// <summary>Invokes <see cref="OpenProcessEditor"/> if set; a no-op otherwise.</summary>
    [RelayCommand]
    private async Task OpenProcessEditorCmd()
    {
        if (OpenProcessEditor is not null) await OpenProcessEditor();
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
