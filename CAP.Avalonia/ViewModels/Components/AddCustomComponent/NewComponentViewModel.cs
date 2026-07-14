using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CAP.Avalonia.Controls.Canvas.ComponentPreview;
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
/// Orchestrates adding a user-authored component to a PDK-first target: a named custom PDK
/// chosen from <see cref="PdkChoices"/> (<see cref="SelectedCustomPdk"/>, process always
/// inherited). A brand-new PDK is never created inline — picking the trailing
/// <see cref="PdkChoice.NewPdkSentinel"/> entry invokes the modal hook <see cref="CreateNewPdk"/>
/// instead (PDK-selection logic lives in the <c>NewComponentViewModel.PdkSelection.cs</c>
/// partial). Renders the component's own Python code (nazca or gdsfactory), optionally
/// recomputes its S-matrix via the FDTD solver, and saves the result as a
/// <see cref="PdkComponentDraft"/>. Never invents physics — a missing solver, an unavailable
/// backend, or a failed solve always saves the component as a black box (no S-matrix), never a
/// fabricated one. The save/FDTD-compute path lives in the <c>NewComponentViewModel.Save.cs</c>
/// partial (kept a separate file for size).
/// </summary>
public partial class NewComponentViewModel : ObservableObject
{
    /// <summary>Pixel budget passed to <see cref="PreviewBitmapFactory.FromResult"/> for the thumbnail.</summary>
    private const int PreviewBitmapPixels = 512;

    private static readonly IReadOnlyList<GeometryBackend> _availableBackends =
        new[] { GeometryBackend.GdsFactory, GeometryBackend.Nazca };

    private readonly ComponentGeometryExtractor _extractor;
    private readonly IFdtdSMatrixService? _fdtd;
    private readonly UserPdkStore _store;

    private GeometryExtractResult? _lastPreview;

    [ObservableProperty] private string _componentName = string.Empty;
    [ObservableProperty] private GeometryBackend _selectedBackend = GeometryBackend.GdsFactory;
    [ObservableProperty] private PdkChoice? _selectedPdkChoice;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private string _code = string.Empty;

    /// <summary>
    /// True when the wizard was opened via <see cref="LoadForEdit"/> to edit an existing custom
    /// component in place, rather than to author a new one. Purely a display flag consumed by
    /// <see cref="WindowTitle"/>/<see cref="SaveButtonLabel"/> — the save path itself
    /// (<c>AppendToExistingPdk</c>, overwrite-by-name) is identical either way.
    /// </summary>
    [ObservableProperty] private bool _isEditMode;

    /// <summary>
    /// Rasterised thumbnail of the last successful <see cref="RunPreview"/>, rendered via
    /// <see cref="PreviewBitmapFactory.FromResult"/>. Null before any preview, after a failed
    /// preview, or when the current environment has no rendering backend (e.g. headless tests) —
    /// all non-fatal, callers must tolerate null.
    /// </summary>
    [ObservableProperty] private Bitmap? _previewBitmap;

    /// <summary>Fabrication processes offered by <see cref="CreateNewPdk"/>'s modal (not chosen here).</summary>
    public IReadOnlyList<ProcessDefinition> Processes { get; }

    /// <summary>Geometry backends selectable for the rendered code: always both, since saving is always own-code.</summary>
    public IReadOnlyList<GeometryBackend> AvailableBackends => _availableBackends;

    /// <summary>Window title: reflects <see cref="IsEditMode"/> so Task 6's view can bind it directly.</summary>
    public string WindowTitle => IsEditMode ? "Edit Component" : "New Component";

    /// <summary>Save button label: reflects <see cref="IsEditMode"/> so Task 6's view can bind it directly.</summary>
    public string SaveButtonLabel => IsEditMode ? "Save changes" : "Save";

    /// <summary>
    /// File-picker hook for <see cref="LoadCodeFromFile"/>: returns a ".py" file's already-read
    /// contents, or null if cancelled. Null keeps the command a no-op — no direct file dialog.
    /// </summary>
    public Func<Task<string?>>? PickPyFile { get; set; }

    /// <summary>The draft last written by <c>Save</c>, or null before a successful save.</summary>
    public PdkComponentDraft? SavedDraft { get; private set; }

    /// <summary>
    /// The user-PDK file path <c>Save</c> actually wrote to (the selected named custom PDK's
    /// file). Null before a successful save.
    /// </summary>
    public string? SavedFilePath { get; private set; }

    /// <summary>Raised after a successful save, so a listener (e.g. the left panel) can refresh.</summary>
    public event EventHandler? Saved;

    /// <summary>
    /// Optional confirmation hook invoked with (componentName, pdkName) when a save would
    /// overwrite an existing component in the target PDK file; returning true proceeds with the
    /// overwrite. When null, a collision is reported via <see cref="StatusText"/> and the save is
    /// aborted.
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmOverwrite { get; set; }

    /// <summary>
    /// Initializes the view model with its collaborators, the fabrication processes offered to
    /// a "create new PDK" modal, and the already-existing named custom PDKs read from
    /// <paramref name="store"/>. Pre-selects the first existing custom PDK, if any; otherwise no
    /// PDK is selected until the user picks one or creates one via <see cref="CreateNewPdk"/>.
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

        RefreshPdkChoices();
        if (AvailableCustomPdks.Count > 0)
        {
            SelectedPdkChoice = PdkChoices[0];
        }

        // Seed the editor with a starter snippet so it is never blank on first open. Save no
        // longer requires a prior explicit Preview click (issue #733 review, Finding 9) — it
        // renders/validates the current code itself via EnsurePreviewAsync — so this starter
        // snippet cannot mask a stale/mismatched preview either way.
        if (string.IsNullOrWhiteSpace(Code))
        {
            Code = BackendCodeExamples.For(SelectedBackend);
        }
    }

    // A change to any input the preview was rendered from invalidates the preview — otherwise
    // a saved draft could be built from a rendered preview that no longer matches the current
    // inputs. Clearing _lastPreview is the load-bearing part: Save re-renders via
    // EnsurePreviewAsync whenever _lastPreview isn't a fresh success, so a stale preview can
    // never be saved verbatim. HasPreview no longer gates Save's enablement (Save renders
    // on demand) — it only drives the preview thumbnail/status display.
    partial void OnSelectedBackendChanged(GeometryBackend value)
    {
        // Autoload the new backend's starter snippet, but only over an empty editor or one
        // still holding the OTHER backend's untouched auto-example — never over user-authored
        // code, which is anything else (including the new backend's own example, re-affirmed).
        var otherBackend = value == GeometryBackend.GdsFactory ? GeometryBackend.Nazca : GeometryBackend.GdsFactory;
        if (string.IsNullOrWhiteSpace(Code) || Code == BackendCodeExamples.For(otherBackend))
        {
            Code = BackendCodeExamples.For(value);
        }
        InvalidatePreview();
    }
    partial void OnCodeChanged(string value) => InvalidatePreview();

    /// <summary>Keeps the display-only <see cref="WindowTitle"/>/<see cref="SaveButtonLabel"/> in sync.</summary>
    partial void OnIsEditModeChanged(bool value)
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(SaveButtonLabel));
    }

    private void InvalidatePreview()
    {
        _lastPreview = null;
        HasPreview = false;
        PreviewBitmap = null;
        // The S-matrix belongs to the geometry it was computed FROM — clearing it here too
        // (issue #733 review, Finding 1, critical) is the load-bearing fix: without it, Save
        // would re-render the NEW geometry (via EnsurePreviewAsync) but still attach the OLD
        // geometry's FDTD result, persisting invented physics that never matched what was
        // actually saved. A geometry change must always force at least a black-box save unless
        // ComputeSMatrix is re-run against the new geometry.
        _computedModel = null;
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

    /// <summary>
    /// Renders the configured geometry reference, extracts its size and pins, and rasterises a
    /// thumbnail into <see cref="PreviewBitmap"/> via <see cref="PreviewBitmapFactory.FromResult"/>.
    /// A failed render (or a render with nothing to rasterise) clears <see cref="PreviewBitmap"/>
    /// rather than leaving a stale bitmap behind. Always re-renders — an explicit Preview click
    /// invalidates any cached result first, unlike <see cref="Save"/>'s own call to
    /// <see cref="EnsurePreviewAsync"/>, which reuses a still-valid one.
    /// </summary>
    [RelayCommand]
    private async Task RunPreview()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            InvalidatePreview();
            await EnsurePreviewAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Ensures <see cref="_lastPreview"/> holds a successful render, rendering one on demand if
    /// it doesn't — the mechanism that lets <see cref="Save"/> (in the
    /// <c>NewComponentViewModel.Save.cs</c> partial) work without a prior explicit
    /// <see cref="RunPreview"/> click. Reuses an already-successful <see cref="_lastPreview"/>
    /// verbatim (so a preceding Preview click is never re-rendered); otherwise renders exactly
    /// like <see cref="RunPreview"/> does, updating <see cref="HasPreview"/>/
    /// <see cref="PreviewBitmap"/>/<see cref="StatusText"/> either way. Does not touch
    /// <see cref="IsBusy"/> itself: both callers already hold that guard for the duration of
    /// their own command, and re-acquiring it here would be redundant at best and, for a
    /// re-entrant caller, a way to defeat the guard.
    /// </summary>
    private async Task<bool> EnsurePreviewAsync()
    {
        if (_lastPreview is { Success: true })
        {
            return true;
        }

        var reference = BuildReference();
        var result = await _extractor.ExtractAsync(reference);
        _lastPreview = result;
        HasPreview = result.Success;
        PreviewBitmap = result.Success
            ? PreviewBitmapFactory.FromResult(result.Raw, PreviewBitmapPixels)
            : null;
        StatusText = result.Success
            ? $"Preview rendered: {result.WidthUm:0.###} x {result.HeightUm:0.###} um, {result.Pins.Count} pins."
            : result.Error ?? "Preview render failed.";
        return result.Success;
    }
}
