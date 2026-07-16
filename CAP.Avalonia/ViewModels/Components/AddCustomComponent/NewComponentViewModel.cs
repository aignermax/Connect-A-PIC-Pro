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

    public event EventHandler? Saved;

    public Func<string, string, Task<bool>>? ConfirmOverwrite { get; set; }

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

    // The edit-mode title includes the component name (task-2), so a rename while the
    // window is open (or the initial LoadForEdit assignment, which sets ComponentName
    // before IsEditMode) must also refresh the title binding.
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
            : result.Error ?? "Preview render failed.";
        return result.Success;
    }
}
