using System.Globalization;
using CAP.Avalonia.Services.ComponentRegistry;
using CAP.Avalonia.Services.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.ComponentRegistry.RegistryBrowser;

/// <summary>
/// "Download" of the registry browser (issue #773): adopts the selected
/// component's real S-parameter artifact into the local process-bound
/// "Registry &lt;process&gt;" user PDK, so it appears in the Component Library
/// and can be placed on the canvas. Only usable (non-withdrawn) artifacts are
/// offered; a <c>disputed</c> artifact requires an explicit in-app
/// confirmation, and components of a different process than the active one
/// stay disabled (single-process lock).
/// </summary>
public partial class RegistryBrowserViewModel
{
    private readonly RegistryDownloadService? _downloadService;

    /// <summary>True while a download is in flight.</summary>
    [ObservableProperty]
    private bool _isDownloading;

    /// <summary>One-line outcome of the last download attempt (green success / red failure).</summary>
    [ObservableProperty]
    private string? _downloadMessage;

    /// <summary>True when <see cref="DownloadMessage"/> reports a failure.</summary>
    [ObservableProperty]
    private bool _downloadIsError;

    /// <summary>Foreground of <see cref="DownloadMessage"/>: green on success, salmon on failure.</summary>
    public string DownloadMessageColor => DownloadIsError ? "Salmon" : "#8fbf8f";

    /// <summary>True while the disputed-artifact warning awaits the user's explicit confirmation.</summary>
    [ObservableProperty]
    private bool _pendingDisputedConfirm;

    /// <summary>In-flight download; awaited by tests.</summary>
    public Task DownloadTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Tooltip text explaining why the download button is disabled; null when
    /// downloading is possible (or when nothing is selected — the whole
    /// section is hidden then).
    /// </summary>
    public string? DownloadUnavailableReason
    {
        get
        {
            if (SelectedComponent is null || Details.Manifest is null || _downloadService is null)
                return null;
            if (!IsProcessCompatible(Details.Manifest.Process))
                return LocalizationService.Instance.Translate("Registry.DownloadProcessMismatch");
            if (RegistryArtifactSelector.Select(Details.Manifest) is null)
                return LocalizationService.Instance.Translate("Registry.NoUsableArtifact");
            return null;
        }
    }

    private bool CanDownloadSelected() =>
        _downloadService != null
        && !IsDownloading
        && SelectedComponent != null
        && Details.Manifest != null
        && RegistryArtifactSelector.Select(Details.Manifest) != null
        && IsProcessCompatible(Details.Manifest.Process);

    /// <summary>
    /// The adopted PDK binds to the manifest's process; with a loaded active
    /// process only components of that same process may be adopted (the
    /// single-process lock would reject any other placement anyway).
    /// </summary>
    private bool IsProcessCompatible(string processId) =>
        string.IsNullOrEmpty(ActiveProcessId) ||
        string.Equals(ActiveProcessId, processId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Re-evaluates the download button's enabled state and tooltip.</summary>
    private void NotifyDownloadStateChanged()
    {
        DownloadCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(DownloadUnavailableReason));
    }

    /// <summary>Adopts the selected component into the local Registry PDK.</summary>
    [RelayCommand(CanExecute = nameof(CanDownloadSelected))]
    private void Download() => DownloadTask = DownloadCoreAsync(disputedConfirmed: false);

    /// <summary>Confirms adoption of a disputed artifact after the warning was shown.</summary>
    [RelayCommand]
    private void ConfirmDisputedDownload()
    {
        PendingDisputedConfirm = false;
        DownloadTask = DownloadCoreAsync(disputedConfirmed: true);
    }

    private async Task DownloadCoreAsync(bool disputedConfirmed)
    {
        var manifest = Details.Manifest;
        var manifestPath = Details.ManifestPath;
        var choice = manifest is null ? null : RegistryArtifactSelector.Select(manifest);
        if (_downloadService is null || manifest is null || manifestPath is null || choice is null)
            return;

        DownloadMessage = null;
        DownloadIsError = false;

        // Disputed data is adopted only after the user explicitly acknowledged
        // the warning — the first click just surfaces it and writes nothing.
        if (choice.IsDisputed && !disputedConfirmed)
        {
            PendingDisputedConfirm = true;
            return;
        }
        PendingDisputedConfirm = false;

        IsDownloading = true;
        try
        {
            var result = await _downloadService.DownloadAsync(manifestPath, manifest, choice);
            DownloadIsError = !result.IsSuccess;
            DownloadMessage = result.IsSuccess
                ? string.Format(CultureInfo.InvariantCulture,
                    LocalizationService.Instance.Translate("Registry.Downloaded"), manifest.Name, result.PdkName)
                : string.Format(CultureInfo.InvariantCulture,
                    LocalizationService.Instance.Translate("Registry.DownloadFailed"), result.ErrorMessage);
        }
        catch (Exception ex)
        {
            DownloadIsError = true;
            DownloadMessage = string.Format(CultureInfo.InvariantCulture,
                LocalizationService.Instance.Translate("Registry.DownloadFailed"), ex.Message);
        }
        finally
        {
            IsDownloading = false;
        }
    }

    partial void OnIsDownloadingChanged(bool value) => NotifyDownloadStateChanged();

    partial void OnDownloadIsErrorChanged(bool value) => OnPropertyChanged(nameof(DownloadMessageColor));
}
