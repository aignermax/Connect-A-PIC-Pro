using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP_Core.Export;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;

namespace CAP.Avalonia.ViewModels.PdkImport;

/// <summary>
/// Represents the current step of the PDK Import Wizard.
/// </summary>
public enum WizardStep
{
    /// <summary>Parsing the Python module (auto-starts on open).</summary>
    Parsing,

    /// <summary>Reviewing the parsed components and selecting which to import.</summary>
    Review,

    /// <summary>Configuring output path and triggering the final save.</summary>
    Save,
}

/// <summary>
/// ViewModel for the PDK Import Wizard dialog.
/// Guides users through parsing a Nazca .py file and saving it as a PDK JSON.
/// Issue #476: PDK Import Wizard with Python/Nazca parser and AI-assisted error correction.
/// </summary>
public partial class PdkImportWizardViewModel : ObservableObject
{
    private readonly PdkImportService _importService;
    private PdkParseResult? _parseResult;

    /// <summary>
    /// Test-only seam to populate the parse result without running an actual
    /// parse. Production code only writes to <c>_parseResult</c> from inside
    /// <see cref="StartParsingAsync"/>.
    /// </summary>
    internal PdkParseResult? ParseResultForTesting
    {
        get => _parseResult;
        set => _parseResult = value;
    }

    /// <summary>Absolute path to the Python .py file being imported.</summary>
    public string PyFilePath { get; }

    /// <summary>Current wizard step controlling which UI panel is visible.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsParsingStep))]
    [NotifyPropertyChangedFor(nameof(IsReviewStep))]
    [NotifyPropertyChangedFor(nameof(IsSaveStep))]
    private WizardStep _currentStep = WizardStep.Parsing;

    /// <summary>True while an async operation (parse or save) is running.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Status text shown during parsing and saving operations.</summary>
    [ObservableProperty]
    private string _statusText = LocalizationService.Instance.Translate("PdkImport.StatusInitializing");

    /// <summary>Error text shown when parsing or saving fails.</summary>
    [ObservableProperty]
    private string _errorText = "";

    /// <summary>True when a recoverable error occurred and retry is possible.</summary>
    [ObservableProperty]
    private bool _hasError;

    /// <summary>PDK name that will appear in the library after import.</summary>
    [ObservableProperty]
    private string _pdkName = "";

    /// <summary>Absolute path for the output JSON file.</summary>
    [ObservableProperty]
    private string _outputPath = "";

    /// <summary>True when the wizard is on the parsing step.</summary>
    public bool IsParsingStep => CurrentStep == WizardStep.Parsing;

    /// <summary>True when the wizard is on the review step.</summary>
    public bool IsReviewStep => CurrentStep == WizardStep.Review;

    /// <summary>True when the wizard is on the save step.</summary>
    public bool IsSaveStep => CurrentStep == WizardStep.Save;

    /// <summary>Parsed components displayed in the review and save steps.</summary>
    public ObservableCollection<ComponentParseResultViewModel> ParsedComponents { get; } = new();

    /// <summary>Invoked when the wizard completes successfully with the saved JSON file path.</summary>
    public Action<string>? OnCompleted { get; set; }

    /// <summary>Invoked when the user cancels the wizard without saving.</summary>
    public Action? OnCancelled { get; set; }

    /// <summary>
    /// Async function to show a save-file dialog. Set by the view layer.
    /// Returns the chosen file path, or null if cancelled.
    /// </summary>
    public Func<Task<string?>>? ShowSaveDialogAsync { get; set; }

    /// <summary>Initializes a new <see cref="PdkImportWizardViewModel"/>.</summary>
    /// <param name="pyFilePath">Absolute path to the Python .py file to import.</param>
    /// <param name="importService">Service that parses and converts PDK data.</param>
    public PdkImportWizardViewModel(string pyFilePath, PdkImportService importService)
    {
        PyFilePath = pyFilePath ?? throw new ArgumentNullException(nameof(pyFilePath));
        _importService = importService ?? throw new ArgumentNullException(nameof(importService));
        PdkName = Path.GetFileNameWithoutExtension(pyFilePath);
        OutputPath = Path.ChangeExtension(pyFilePath, ".json");
    }

    /// <summary>
    /// Starts the parse operation. Called automatically when the dialog opens.
    /// On success, advances to the Review step. Safe against double-click /
    /// re-entry: a second call while the first is still running is a no-op,
    /// preventing a race between two `ParsedComponents.Clear()` + `Add` loops.
    /// </summary>
    public async Task StartParsingAsync()
    {
        if (IsLoading) return;

        IsLoading = true;
        HasError = false;
        ErrorText = "";
        CurrentStep = WizardStep.Parsing;
        ParsedComponents.Clear();
        StatusText = LocalizationService.Instance.Translate("PdkImport.StatusStartingParser");

        try
        {
            var progress = new Progress<string>(msg => StatusText = msg);
            _parseResult = await _importService.ParseFromFileAsync(PyFilePath, progress);

            foreach (var comp in _parseResult.Components)
                ParsedComponents.Add(new ComponentParseResultViewModel(comp));

            if (!string.IsNullOrWhiteSpace(_parseResult.Name))
                PdkName = _parseResult.Name;

            var warningCount = ParsedComponents.Count(c => c.HasWarnings);
            StatusText = warningCount > 0
                ? string.Format(
                    LocalizationService.Instance.Translate("PdkImport.StatusParsedWithWarnings"),
                    ParsedComponents.Count, warningCount)
                : string.Format(
                    LocalizationService.Instance.Translate("PdkImport.StatusParsedSuccess"),
                    ParsedComponents.Count);

            CurrentStep = WizardStep.Review;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            HasError = true;
            StatusText = LocalizationService.Instance.Translate("PdkImport.StatusParsingFailed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Advances from the Review step to the Save step.</summary>
    [RelayCommand]
    private void ProceedToSave()
    {
        CurrentStep = WizardStep.Save;
    }

    /// <summary>Returns from the Save step back to the Review step.</summary>
    [RelayCommand]
    private void BackToReview()
    {
        CurrentStep = WizardStep.Review;
    }

    /// <summary>Clears state and re-runs parsing from scratch.</summary>
    [RelayCommand]
    private async Task RetryParsing()
    {
        await StartParsingAsync();
    }

    /// <summary>Opens a save file dialog so the user can choose the output JSON path.</summary>
    [RelayCommand]
    private async Task BrowseOutputPath()
    {
        if (ShowSaveDialogAsync == null) return;
        var path = await ShowSaveDialogAsync();
        if (!string.IsNullOrEmpty(path))
            OutputPath = path;
    }

    /// <summary>
    /// Converts the selected components to a PDK JSON file and invokes <see cref="OnCompleted"/>.
    /// No-ops with a visible status message when the parse hasn't completed yet,
    /// output path is blank, or the user has unchecked every component — all
    /// three cases would otherwise either silently do nothing or write a
    /// components-less JSON that later fails to load.
    /// </summary>
    [RelayCommand]
    private async Task SaveAndLoad()
    {
        if (_parseResult == null)
        {
            StatusText = LocalizationService.Instance.Translate("PdkImport.StatusParsingNotFinished");
            return;
        }
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            StatusText = LocalizationService.Instance.Translate("PdkImport.StatusChooseOutputPath");
            return;
        }
        if (!ParsedComponents.Any(c => c.IsSelected))
        {
            StatusText = LocalizationService.Instance.Translate("PdkImport.StatusSelectComponent");
            return;
        }

        IsLoading = true;
        HasError = false;
        StatusText = LocalizationService.Instance.Translate("PdkImport.StatusSavingJson");

        try
        {
            var filteredResult = FilterToSelected(_parseResult);
            filteredResult.Name = PdkName;

            var draft = _importService.ConvertToPdkDraft(filteredResult);
            await _importService.SaveToJsonAsync(draft, OutputPath);

            StatusText = string.Format(
                LocalizationService.Instance.Translate("PdkImport.StatusSaved"),
                draft.Components.Count, Path.GetFileName(OutputPath));
            OnCompleted?.Invoke(OutputPath);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            HasError = true;
            StatusText = LocalizationService.Instance.Translate("PdkImport.StatusSaveFailed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Cancels the wizard without saving.</summary>
    [RelayCommand]
    private void Cancel()
    {
        OnCancelled?.Invoke();
    }

    /// <summary>
    /// Returns a copy of <paramref name="result"/> filtered to only the components
    /// whose corresponding VM entry is selected. Filtering is done by positional
    /// index instead of by name: two parametric Nazca cells can produce
    /// identical <c>Name</c> values, and a name-based <see cref="HashSet{T}"/>
    /// would either include both entries when one is unchecked, or exclude
    /// both when one is checked.
    /// </summary>
    internal PdkParseResult FilterToSelected(PdkParseResult result)
    {
        var selectedComponents = new List<ParsedComponentGeometry>();
        int count = Math.Min(result.Components.Count, ParsedComponents.Count);
        for (int i = 0; i < count; i++)
        {
            if (ParsedComponents[i].IsSelected)
                selectedComponents.Add(result.Components[i]);
        }

        return new PdkParseResult
        {
            FileFormatVersion = result.FileFormatVersion,
            Name = result.Name,
            Description = result.Description,
            Foundry = result.Foundry,
            Version = result.Version,
            DefaultWavelengthNm = result.DefaultWavelengthNm,
            NazcaModuleName = result.NazcaModuleName,
            Components = selectedComponents,
        };
    }
}
