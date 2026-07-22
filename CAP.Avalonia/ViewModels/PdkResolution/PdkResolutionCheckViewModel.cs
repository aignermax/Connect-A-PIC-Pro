using System.Collections.ObjectModel;
using System.Text;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Export.PdkResolution;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.PdkResolution;

/// <summary>
/// ViewModel for "Tools → Check PDKs against Python" (issue #515): loads every
/// bundled PDK JSON, verifies each <c>nazcaFunction</c> string against the
/// installed Python packages, and reports the mismatches per PDK.
/// </summary>
public partial class PdkResolutionCheckViewModel : ObservableObject
{
    /// <summary>Sentinel nazcaFunction of virtual analysis tools — never exported, skip.</summary>
    private const string AnalyzerSentinel = "__analyzer__";

    private readonly PdkLoader _pdkLoader;
    private readonly PdkFunctionResolutionService _resolutionService;
    private readonly Func<string?> _pdkDirectoryResolver;

    /// <summary>Set by the dialog code-behind to the Avalonia clipboard bridge.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>True while the check subprocess is running.</summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>Status line shown at the bottom of the dialog.</summary>
    [ObservableProperty]
    private string _statusText = LocalizationService.Instance.Translate("PdkResolution.StatusInitial");

    /// <summary>True when at least one row failed — enables the copy-to-clipboard button.</summary>
    [ObservableProperty]
    private bool _hasFailures;

    /// <summary>Per-PDK result groups.</summary>
    public ObservableCollection<PdkResolutionGroupViewModel> Pdks { get; } = new();

    /// <summary>
    /// Initializes the ViewModel.
    /// </summary>
    /// <param name="pdkLoader">Loader for PDK JSON files.</param>
    /// <param name="resolutionService">Python-backed nazcaFunction resolver.</param>
    /// <param name="pdkDirectoryResolver">
    /// Optional override for the PDK directory (tests). Defaults to the
    /// bundled-PDK directory resolution used by the component library.
    /// </param>
    public PdkResolutionCheckViewModel(
        PdkLoader pdkLoader,
        PdkFunctionResolutionService resolutionService,
        Func<string?>? pdkDirectoryResolver = null)
    {
        _pdkLoader = pdkLoader;
        _resolutionService = resolutionService;
        _pdkDirectoryResolver = pdkDirectoryResolver
            ?? (() => LeftPanelViewModel.ResolveBundledPdkDirectory(AppDomain.CurrentDomain.BaseDirectory));
    }

    /// <summary>Runs the consistency check over every PDK JSON in the bundled directory.</summary>
    [RelayCommand]
    private async Task RunCheckAsync()
    {
        IsRunning = true;
        Pdks.Clear();
        HasFailures = false;
        StatusText = LocalizationService.Instance.Translate("PdkResolution.StatusChecking");
        try
        {
            var pdkDir = _pdkDirectoryResolver();
            if (pdkDir == null || !Directory.Exists(pdkDir))
            {
                StatusText = LocalizationService.Instance.Translate("PdkResolution.StatusNoDirectory");
                return;
            }

            foreach (var file in Directory.GetFiles(pdkDir, "*.json").OrderBy(f => f))
                Pdks.Add(await CheckPdkFileAsync(file));

            var errors = Pdks.SelectMany(p => p.Rows).Count(r => r.Status == PdkResolutionStatus.Error);
            var warnings = Pdks.SelectMany(p => p.Rows).Count(r => r.Status == PdkResolutionStatus.Warning);
            HasFailures = errors > 0 || warnings > 0 || Pdks.Any(p => p.HasError);
            StatusText = string.Format(
                LocalizationService.Instance.Translate("PdkResolution.StatusChecked"),
                Pdks.Count, errors, warnings);
        }
        catch (Exception ex)
        {
            // e.g. Directory.GetFiles throwing (permissions / TOCTOU) — without this the async
            // command would fault unobserved, leaving the UI stuck on "Checking…" (#515 review).
            HasFailures = true;
            StatusText = string.Format(
                LocalizationService.Instance.Translate("PdkResolution.StatusCheckFailed"), ex.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task<PdkResolutionGroupViewModel> CheckPdkFileAsync(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        PdkDraft draft;
        try
        {
            // Tolerant load: the check must also work on PDKs whose offsets
            // are still missing — that's exactly the state it helps fix.
            draft = _pdkLoader.LoadFromFileForEditing(filePath);
        }
        catch (Exception ex)
        {
            return new PdkResolutionGroupViewModel
            {
                PdkName = fileName,
                FileName = fileName,
                Error = string.Format(
                    LocalizationService.Instance.Translate("PdkResolution.StatusFailedToLoad"), ex.Message)
            };
        }

        var group = new PdkResolutionGroupViewModel { PdkName = draft.Name, FileName = fileName };
        var components = draft.Components.Where(c => c.NazcaFunction != AnalyzerSentinel).ToList();
        if (components.Count == 0)
            return group;

        // A component exports via its nazcaFunction, or — for gdsfactory-native PDKs like
        // CornerStone — via its gdsFactoryFunction (e.g. "cspdk.sin300.coupler"). Check whichever
        // it actually uses; otherwise gdsfactory PDKs would show every row red "empty nazcaFunction"
        // (#515 review). The generic importlib resolver handles the gdsfactory dotted path.
        var useGdsFactory = components
            .Select(c => string.IsNullOrWhiteSpace(c.NazcaFunction) && !string.IsNullOrWhiteSpace(c.GdsFactoryFunction))
            .ToList();
        var functionPaths = components
            .Select((c, i) => useGdsFactory[i] ? c.GdsFactoryFunction! : c.NazcaFunction ?? "")
            .ToList();

        var entries = functionPaths
            .Select((path, i) =>
            {
                var (module, function) = NazcaFunctionPath.Split(path);
                return new PdkResolutionEntry
                {
                    Name = components[i].Name,
                    Module = module,
                    Function = function,
                    Backend = useGdsFactory[i] ? "gdsfactory" : "nazca"
                };
            })
            .ToList();

        var report = await _resolutionService.ResolveAsync(entries);
        if (!report.Success)
            return new PdkResolutionGroupViewModel
            { PdkName = draft.Name, FileName = fileName, Error = report.Error };

        for (var i = 0; i < components.Count; i++)
        {
            // Results come back in request order; guard against a short list.
            var result = i < report.Results.Count ? report.Results[i] : null;
            group.Rows.Add(new PdkResolutionRowViewModel
            {
                ComponentName = components[i].Name,
                FunctionPath = functionPaths[i],
                Status = result?.Status ?? PdkResolutionStatus.Error,
                Message = result?.Message
                    ?? LocalizationService.Instance.Translate("PdkResolution.StatusNoResult")
            });
        }
        return group;
    }

    /// <summary>Copies the failing entries to the clipboard as a PR-ready punch list.</summary>
    [RelayCommand]
    private async Task CopyFailingListAsync()
    {
        var text = BuildFailingListText();
        if (text.Length == 0)
            return;
        if (CopyToClipboard == null)
        {
            StatusText = LocalizationService.Instance.Translate("PdkResolution.StatusClipboardUnavailable");
            return;
        }
        await CopyToClipboard(text);
        StatusText = LocalizationService.Instance.Translate("PdkResolution.StatusCopied");
    }

    /// <summary>
    /// Builds the plain-text list of all non-OK entries, grouped by PDK file.
    /// Internal so tests can verify the format without a clipboard.
    /// </summary>
    internal string BuildFailingListText()
    {
        var sb = new StringBuilder();
        foreach (var pdk in Pdks)
        {
            var failures = pdk.Rows.Where(r => r.IsFailure).ToList();
            if (pdk.Error == null && failures.Count == 0)
                continue;
            sb.AppendLine($"{pdk.FileName} ({pdk.PdkName}):");
            if (pdk.Error != null)
                sb.AppendLine($"  ERROR: {pdk.Error}");
            foreach (var row in failures)
                sb.AppendLine($"  {row.StatusBadge} {row.ComponentName} → \"{row.FunctionPath}\": {row.Message}");
        }
        return sb.ToString();
    }
}
