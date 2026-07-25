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

public partial class NewComponentViewModel : ObservableObject
{
    private const int PreviewBitmapPixels = 512;

    private static readonly IReadOnlyList<GeometryBackend> _availableBackends =
        new[] { GeometryBackend.GdsFactory, GeometryBackend.Nazca };

    private readonly ComponentGeometryExtractor _extractor;
    private readonly IFdtdSMatrixService? _fdtd;
    private readonly UserPdkStore _store;
    private readonly CAP_Core.ErrorConsoleService? _errorConsole;

    private GeometryExtractResult? _lastPreview;

    [ObservableProperty] private string _componentName = string.Empty;
    [ObservableProperty] private GeometryBackend _selectedBackend = GeometryBackend.GdsFactory;
    [ObservableProperty] private PdkChoice? _selectedPdkChoice;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private string _code = string.Empty;

    [ObservableProperty] private bool _isEditMode;

    [ObservableProperty] private Bitmap? _previewBitmap;

    public IReadOnlyList<ProcessDefinition> Processes { get; }

    public IReadOnlyList<GeometryBackend> AvailableBackends => _availableBackends;

    public string WindowTitle => IsEditMode ? $"Edit Component: {ComponentName}" : "New Component";

    public string SaveButtonLabel => IsEditMode ? "Save changes" : "Save";

    public Func<Task<string?>>? PickPyFile { get; set; }

    public PdkComponentDraft? SavedDraft { get; private set; }

    public string? SavedFilePath { get; private set; }

    /// <summary>
    /// True when the last save executed the deferred bundled fork: only then may the library
    /// shadow the bundled PDK with the saved file — a mere name match must not.
    /// </summary>
    public bool SavedViaPendingBundledFork { get; private set; }

    public event EventHandler? Saved;

    public Func<string, string, Task<bool>>? ConfirmOverwrite { get; set; }

    public NewComponentViewModel(
        ComponentGeometryExtractor extractor,
        IFdtdSMatrixService? fdtd,
        UserPdkStore store,
        IReadOnlyList<ProcessDefinition> processes,
        CAP_Core.ErrorConsoleService? errorConsole = null,
        Services.Solvers.FdtdBackendRegistry? fdtdBackendRegistry = null,
        Services.IUrlLauncher? urlLauncher = null)
    {
        _extractor = extractor;
        _fdtd = fdtd;
        _store = store;
        _errorConsole = errorConsole;
        Processes = processes;
        InitFdtdBackendSelection(fdtdBackendRegistry, urlLauncher);

        RefreshPdkChoices();
        if (AvailableCustomPdks.Count > 0)
        {
            SelectedPdkChoice = PdkChoices[0];
        }

        if (string.IsNullOrWhiteSpace(Code))
        {
            Code = BackendCodeExamples.For(SelectedBackend);
        }
    }

    partial void OnSelectedBackendChanged(GeometryBackend value)
    {
        var otherBackend = value == GeometryBackend.GdsFactory ? GeometryBackend.Nazca : GeometryBackend.GdsFactory;
        if (string.IsNullOrWhiteSpace(Code) || Code == BackendCodeExamples.For(otherBackend))
        {
            Code = BackendCodeExamples.For(value);
        }
        InvalidatePreview();
    }
    partial void OnCodeChanged(string value) => InvalidatePreview();

    // The edit-mode title includes the component name, so a rename must refresh the title binding.
    partial void OnComponentNameChanged(string value) => OnPropertyChanged(nameof(WindowTitle));

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
        _computedModel = null;
        RefreshSMatrixEntries();
    }

    private GeometryReference BuildReference() => GeometryReference.RawCode(SelectedBackend, Code);

    [RelayCommand]
    private async Task LoadCodeFromFile()
    {
        if (PickPyFile is null) return;
        var content = await PickPyFile();
        if (content is not null) Code = content;
    }

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
            : DescribeRenderError(result.Error);
        return result.Success;
    }

    /// <summary>
    /// Single source for the preview-failure message (field round 6): the window status shows
    /// the actionable message (the foundry-package hint when recognised, otherwise the raw
    /// error) and the Error Console receives the SAME message plus the raw Python detail —
    /// never two different stories for one failure.
    /// </summary>
    private string DescribeRenderError(string? rawError)
    {
        var hint = CAP_Core.Export.FoundryEnvironmentErrorHint.Describe(rawError);
        var display = hint ?? rawError ?? "Preview render failed.";
        var detail = hint is not null && !string.IsNullOrWhiteSpace(rawError)
            ? $"{display}\nPython error detail: {rawError}"
            : display;
        _errorConsole?.LogError($"Component preview render failed: {detail}");
        return display;
    }
}
