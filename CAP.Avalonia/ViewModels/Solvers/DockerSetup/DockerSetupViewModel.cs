using CAP.Avalonia.Services;
using CAP_Core.Solvers.Fdtd;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Solvers.DockerSetup;

/// <summary>
/// ViewModel for the guided "Set up FDTD" dialog shown when Docker is missing
/// or the engine is stopped. Offers platform-aware guidance (copyable install /
/// start commands on Linux, a download / start hint for Docker Desktop on
/// Windows and macOS) and a "Check again" action that re-probes availability
/// and requests the dialog to close once Docker is ready.
/// </summary>
public partial class DockerSetupViewModel : ObservableObject
{
    /// <summary>Docker Desktop install page (Windows / macOS).</summary>
    public const string DockerDesktopUrl = "https://www.docker.com/products/docker-desktop/";

    /// <summary>Copyable Linux install commands (official convenience script + docker group).</summary>
    public const string LinuxInstallCommands =
        "curl -fsSL https://get.docker.com | sudo sh\nsudo usermod -aG docker $USER";

    /// <summary>Copyable Linux command to start a stopped Docker engine.</summary>
    public const string LinuxStartCommand = "sudo systemctl start docker";

    /// <summary>Copyable Linux command to grant the current user Docker access.</summary>
    public const string LinuxPermissionCommand = "sudo usermod -aG docker $USER";

    private readonly Func<CancellationToken, Task<FdtdAvailability>> _checkAvailability;
    private readonly IUrlLauncher _urlLauncher;

    /// <summary>
    /// Clipboard write callback, wired by the view's code-behind via
    /// <c>TopLevel.GetTopLevel(this).Clipboard</c>. Null in tests unless injected.
    /// </summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>Raised when the dialog should close (Docker became available).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>True on Linux; selects the copyable-command guidance branch.</summary>
    public bool IsLinux { get; }

    /// <summary>True after a successful re-check — the caller can continue its FDTD run.</summary>
    public bool IsDockerAvailable { get; private set; }

    /// <summary>Availability message shown at the top of the dialog.</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>True while "Check again" is probing Docker.</summary>
    [ObservableProperty]
    private bool _isChecking;

    /// <summary>True when Docker is not installed (or not on PATH).</summary>
    [ObservableProperty]
    private bool _isNotInstalled;

    /// <summary>True when Docker is installed but its engine is not running.</summary>
    [ObservableProperty]
    private bool _isEngineStopped;

    /// <summary>True when the engine runs but the user lacks Docker socket access
    /// (Linux: docker-group membership pending until re-login).</summary>
    [ObservableProperty]
    private bool _isPermissionDenied;

    /// <summary>One-shot feedback after a copy action ("Copied to clipboard.").</summary>
    [ObservableProperty]
    private string _copyFeedback = string.Empty;

    /// <summary>Linux + not installed: show the copyable install command block.</summary>
    public bool ShowLinuxInstall => IsLinux && IsNotInstalled;

    /// <summary>Windows/macOS + not installed: show the "Download Docker Desktop" button.</summary>
    public bool ShowDesktopInstall => !IsLinux && IsNotInstalled;

    /// <summary>Linux + engine stopped: show the copyable systemctl start command.</summary>
    public bool ShowLinuxStart => IsLinux && IsEngineStopped;

    /// <summary>Windows/macOS + engine stopped: show the "Start Docker Desktop" hint.</summary>
    public bool ShowDesktopStart => !IsLinux && IsEngineStopped;

    /// <summary>Linux + socket permission denied: show the docker-group / re-login guidance.</summary>
    public bool ShowLinuxPermission => IsLinux && IsPermissionDenied;

    /// <summary>Install commands exposed for AXAML binding.</summary>
    public string LinuxInstallCommandsText => LinuxInstallCommands;

    /// <summary>Start command exposed for AXAML binding.</summary>
    public string LinuxStartCommandText => LinuxStartCommand;

    /// <summary>Permission command exposed for AXAML binding.</summary>
    public string LinuxPermissionCommandText => LinuxPermissionCommand;

    /// <summary>Initialises the ViewModel.</summary>
    /// <param name="checkAvailability">Re-probe used by the "Check again" command
    /// (typically <c>IFdtdSMatrixService.CheckAvailabilityAsync</c>).</param>
    /// <param name="urlLauncher">Opens the Docker Desktop download page in the browser.</param>
    /// <param name="isLinux">Platform override for tests; defaults to the current OS.</param>
    public DockerSetupViewModel(
        Func<CancellationToken, Task<FdtdAvailability>> checkAvailability,
        IUrlLauncher urlLauncher,
        bool? isLinux = null)
    {
        _checkAvailability = checkAvailability ?? throw new ArgumentNullException(nameof(checkAvailability));
        _urlLauncher = urlLauncher ?? throw new ArgumentNullException(nameof(urlLauncher));
        IsLinux = isLinux ?? OperatingSystem.IsLinux();
    }

    /// <summary>Seeds the dialog state from the availability probe that triggered it.</summary>
    public void Initialize(FdtdAvailability availability) => ApplyAvailability(availability);

    /// <summary>Copies the Linux install commands to the clipboard.</summary>
    [RelayCommand]
    private Task CopyInstallCommands() => CopyAsync(LinuxInstallCommands);

    /// <summary>Copies the Linux engine-start command to the clipboard.</summary>
    [RelayCommand]
    private Task CopyStartCommand() => CopyAsync(LinuxStartCommand);

    /// <summary>Copies the Linux docker-group command to the clipboard.</summary>
    [RelayCommand]
    private Task CopyPermissionCommand() => CopyAsync(LinuxPermissionCommand);

    /// <summary>Opens the Docker Desktop download page in the default browser.</summary>
    [RelayCommand]
    private void OpenDownloadPage() => _urlLauncher.Open(DockerDesktopUrl);

    /// <summary>
    /// Re-runs the availability probe. When Docker has become available, flags
    /// success and asks the dialog to close so the original FDTD run continues.
    /// </summary>
    [RelayCommand]
    private async Task CheckAgain()
    {
        IsChecking = true;
        CopyFeedback = string.Empty;
        StatusMessage = "Checking Docker…";
        try
        {
            var availability = await _checkAvailability(CancellationToken.None);
            ApplyAvailability(availability);
            if (availability.IsAvailable)
            {
                IsDockerAvailable = true;
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Docker check failed: {ex.Message}";
        }
        finally
        {
            IsChecking = false;
        }
    }

    private async Task CopyAsync(string text)
    {
        if (CopyToClipboard == null)
            return;
        await CopyToClipboard(text);
        CopyFeedback = "Copied to clipboard.";
    }

    private void ApplyAvailability(FdtdAvailability availability)
    {
        StatusMessage = availability.Message;
        IsNotInstalled = !availability.IsAvailable
            && availability.Reason == FdtdUnavailableReason.NotInstalled;
        IsPermissionDenied = !availability.IsAvailable
            && availability.Reason == FdtdUnavailableReason.PermissionDenied;
        // Treat an unknown reason like a stopped engine: Docker responded enough to
        // be found, so "start it" is the safest actionable hint.
        IsEngineStopped = !availability.IsAvailable && !IsNotInstalled && !IsPermissionDenied;
    }

    partial void OnIsNotInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLinuxInstall));
        OnPropertyChanged(nameof(ShowDesktopInstall));
    }

    partial void OnIsEngineStoppedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLinuxStart));
        OnPropertyChanged(nameof(ShowDesktopStart));
    }

    partial void OnIsPermissionDeniedChanged(bool value)
        => OnPropertyChanged(nameof(ShowLinuxPermission));
}
