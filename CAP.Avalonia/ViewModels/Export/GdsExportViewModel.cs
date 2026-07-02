using CAP_Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP_Core.Export;
using System.Collections.ObjectModel;

namespace CAP.Avalonia.ViewModels.Export;

/// <summary>
/// ViewModel for GDS export functionality.
/// Allows users to export designs to GDS format with automatic Python/Nazca execution.
/// </summary>
public partial class GdsExportViewModel : ObservableObject
{
    private readonly GdsExportService _exportService;
    private readonly PythonDiscoveryService _discoveryService;

    [ObservableProperty]
    private bool _pythonAvailable;

    [ObservableProperty]
    private string _pythonStatus = "Checking...";

    [ObservableProperty]
    private bool _nazcaAvailable;

    [ObservableProperty]
    private string _nazcaStatus = "Checking...";

    [ObservableProperty]
    private bool _generateGdsEnabled = true;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private string _lastExportStatus = string.Empty;

    [ObservableProperty]
    private string _customPythonPath = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private ObservableCollection<PythonDiscoveryService.PythonInstallation> _availablePythons = new();

    [ObservableProperty]
    private string _pythonPathSource = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ManagedEnvCandidate> _managedCandidates = new();

    [ObservableProperty]
    private bool _showNazcaInstallOffer;

    [ObservableProperty]
    private ObservableCollection<InterpreterOption> _interpreterOptions = new();

    [ObservableProperty]
    private bool _showCreateEnvironmentButton;

    /// <summary>
    /// True if both Python and Nazca are available and ready.
    /// </summary>
    public bool IsEnvironmentReady => PythonAvailable && NazcaAvailable;

    /// <summary>
    /// Callback to save Python path to preferences when changed.
    /// </summary>
    public Action<string?>? OnPythonPathChanged { get; set; }

    /// <summary>
    /// Supplies the managed environments that carry Nazca (wired by the DI layer;
    /// the GDS-export slice never references the environment-manager slice directly).
    /// </summary>
    public Func<IReadOnlyList<ManagedEnvCandidate>>? ManagedEnvironmentsProvider { get; set; }

    /// <summary>Activates a managed environment by name (wired by the DI layer).</summary>
    public Action<string>? ActivateManagedEnvironment { get; set; }

    /// <summary>Starts the default Nazca environment installation (wired by the DI layer).</summary>
    public Action? RequestNazcaInstall { get; set; }

    private readonly ErrorConsoleService? _errorConsole;

    /// <summary>Initializes a new instance of <see cref="GdsExportViewModel"/>.</summary>
    /// <param name="exportService">GDS export service.</param>
    /// <param name="errorConsole">Optional service for error logging.</param>
    public GdsExportViewModel(GdsExportService exportService, ErrorConsoleService? errorConsole = null)
    {
        _exportService = exportService;
        _discoveryService = new PythonDiscoveryService();
        _errorConsole = errorConsole;
    }

    /// <summary>
    /// Initializes the ViewModel with saved preferences. A null path clears the
    /// configuration (e.g. the active managed environment was deleted) so a stale
    /// interpreter path never lingers; the next refresh picks a fallback.
    /// </summary>
    /// <param name="savedPythonPath">Previously saved Python path, or null to clear.</param>
    public void Initialize(string? savedPythonPath)
    {
        if (!string.IsNullOrEmpty(savedPythonPath))
        {
            CustomPythonPath = savedPythonPath;
            _exportService.SetCustomPythonPath(savedPythonPath);
            PythonPathSource = "Custom";
            return;
        }

        CustomPythonPath = string.Empty;
        _exportService.SetCustomPythonPath(null);
        PythonPathSource = string.Empty;
    }

    /// <summary>
    /// Checks the Python/Nazca environment and updates status. Exceptions
    /// are surfaced on the status strings and logged to the ErrorConsole —
    /// this method is also invoked fire-and-forget from MainViewModel wiring,
    /// where an unobserved task exception would crash silently.
    /// </summary>
    [RelayCommand]
    public async Task CheckEnvironmentAsync()
    {
        IsChecking = true;
        PythonStatus = "Checking...";
        NazcaStatus = "Checking...";

        try
        {
            var envInfo = await _exportService.CheckPythonEnvironmentAsync();

            PythonAvailable = envInfo.PythonAvailable;
            NazcaAvailable = envInfo.NazcaAvailable;

            if (envInfo.PythonAvailable)
            {
                PythonStatus = $"✓ Found (v{envInfo.PythonVersion})";
            }
            else
            {
                PythonStatus = "✗ Not found";
            }

            if (envInfo.NazcaAvailable)
            {
                NazcaStatus = $"✓ Found (v{envInfo.NazcaVersion})";
            }
            else if (envInfo.PythonAvailable)
            {
                NazcaStatus = "✗ Not installed";
            }
            else
            {
                NazcaStatus = "N/A (Python not found)";
            }

            OnPropertyChanged(nameof(IsEnvironmentReady));
            RefreshManagedCandidates();
        }
        catch (Exception ex)
        {
            PythonAvailable = false;
            NazcaAvailable = false;
            PythonStatus = $"✗ Check failed: {ex.Message}";
            NazcaStatus = "✗ Check failed";
            _errorConsole?.LogError($"Python environment check failed: {ex.Message}", ex);
            OnPropertyChanged(nameof(IsEnvironmentReady));
            RefreshManagedCandidates();
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>
    /// Rebuilds <see cref="ManagedCandidates"/> from <see cref="ManagedEnvironmentsProvider"/>
    /// and recomputes whether the "install Nazca" offer should be shown (no Nazca in the
    /// active interpreter AND no managed environment to switch to).
    /// </summary>
    public void RefreshManagedCandidates()
    {
        ManagedCandidates.Clear();
        foreach (var candidate in ManagedEnvironmentsProvider?.Invoke() ?? Array.Empty<ManagedEnvCandidate>())
            ManagedCandidates.Add(candidate);

        ShowNazcaInstallOffer = !NazcaAvailable && ManagedCandidates.Count == 0;
        // The one-click create stays available whenever no managed environment exists,
        // even if a system Python happens to carry Nazca.
        ShowCreateEnvironmentButton = ManagedCandidates.Count == 0;
        RebuildInterpreterOptions();
    }

    /// <summary>
    /// Rebuilds the unified interpreter list (managed environments first, then discovered
    /// system Pythons), marking the entry that matches the configured interpreter path.
    /// </summary>
    private void RebuildInterpreterOptions()
    {
        InterpreterOptions.Clear();
        foreach (var candidate in ManagedCandidates)
            InterpreterOptions.Add(new InterpreterOption(
                candidate.DisplayText,
                candidate.PythonExecutable,
                IsConfiguredPath(candidate.PythonExecutable),
                candidate.Name));

        foreach (var python in AvailablePythons)
            InterpreterOptions.Add(new InterpreterOption(
                $"{python.Source} · Python {python.PythonVersion ?? "?"} · Nazca {python.NazcaVersion ?? "—"}",
                python.Path,
                IsConfiguredPath(python.Path),
                ManagedName: null));
    }

    private bool IsConfiguredPath(string path) =>
        string.Equals(path, CustomPythonPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Selects an interpreter from the unified list: managed environments are activated
    /// through the registry (persists + updates the running pipeline), system Pythons via
    /// the classic path selection. Both re-check the environment afterwards.
    /// </summary>
    /// <param name="option">The interpreter the user clicked.</param>
    [RelayCommand]
    public async Task SelectInterpreter(InterpreterOption option)
    {
        if (option.ManagedName != null)
        {
            ActivateManagedEnvironment?.Invoke(option.ManagedName);
            await CheckEnvironmentAsync();
            return;
        }

        await SetPythonPathAsync(option.Path);
    }

    /// <summary>
    /// One-stop refresh for the settings page: discovers system Pythons with Nazca
    /// (auto-selecting the first hit when nothing is configured yet — the "first start"
    /// default), then re-checks the configured interpreter and rebuilds the list.
    /// </summary>
    [RelayCommand]
    public async Task RefreshInterpretersAsync()
    {
        await SearchForPythonAsync();
        await CheckEnvironmentAsync();
        await TrySelectFallbackInterpreterAsync();
    }

    /// <summary>
    /// When no interpreter is configured — or the configured one no longer exists on
    /// disk (e.g. its managed environment was deleted) — falls back to the first
    /// available candidate (managed environments come first) instead of showing
    /// "not found" while working alternatives sit in the list.
    /// </summary>
    public async Task TrySelectFallbackInterpreterAsync()
    {
        var configuredIsUsable = !string.IsNullOrEmpty(CustomPythonPath) && File.Exists(CustomPythonPath);
        if (configuredIsUsable) return;

        var fallback = InterpreterOptions.FirstOrDefault();
        if (fallback != null)
            await SelectInterpreter(fallback);
    }

    /// <summary>
    /// Activates a managed environment: the registry callback (DI layer) persists the
    /// interpreter and pushes it into this ViewModel, then the status is re-checked.
    /// </summary>
    /// <param name="candidate">The managed environment to activate.</param>
    [RelayCommand]
    public async Task SelectManagedEnvironment(ManagedEnvCandidate candidate)
    {
        ActivateManagedEnvironment?.Invoke(candidate.Name);
        await CheckEnvironmentAsync();
    }

    /// <summary>Requests creation + installation of the default Nazca environment.</summary>
    [RelayCommand]
    public void InstallNazca() => RequestNazcaInstall?.Invoke();

    /// <summary>
    /// Decides how to proceed with GDS generation based on the current environment
    /// state. Callers refresh the state (<see cref="CheckEnvironmentAsync"/>) first;
    /// this method only maps state + user choice to a decision.
    /// </summary>
    /// <param name="messageBox">Dialog service; null (headless) proceeds like before.</param>
    public async Task<GdsPreflightDecision> PreflightGdsAsync(Services.IMessageBoxService? messageBox)
    {
        if (!GenerateGdsEnabled || IsEnvironmentReady || messageBox == null)
            return GdsPreflightDecision.Proceed;

        var choice = await messageBox.ShowChoicePromptAsync(
            "Nazca is required to generate a GDS file, but no Python environment with Nazca is available. "
            + "The Nazca Python script itself has been exported.",
            "Nazca required",
            new[] { "Install Nazca now", "Open Settings", "Skip GDS" });

        return choice switch
        {
            0 => GdsPreflightDecision.InstallRequested,
            1 => GdsPreflightDecision.OpenSettingsRequested,
            _ => GdsPreflightDecision.SkipGds,
        };
    }

    /// <summary>
    /// Exports a Python script to GDS (if enabled and environment is ready).
    /// </summary>
    /// <param name="scriptPath">Path to the exported Python script.</param>
    /// <returns>Export result with status information.</returns>
    public async Task<GdsExportService.ExportResult> ExportScriptToGdsAsync(string scriptPath)
    {
        var result = await _exportService.ExportToGdsAsync(scriptPath, GenerateGdsEnabled);
        LastExportStatus = result.Status;
        return result;
    }

    private int _searchGeneration;

    /// <summary>
    /// Runs the actual interpreter discovery. Virtual so tests can substitute a
    /// controllable discovery to exercise overlapping-search behavior.
    /// </summary>
    protected virtual Task<List<PythonDiscoveryService.PythonInstallation>> DiscoverPythonsAsync() =>
        _discoveryService.DiscoverPythonWithNazcaAsync();

    /// <summary>
    /// Searches for Python installations with Nazca and updates the available list.
    /// Starting a new search supersedes any still-running one: the stale run discards
    /// its results, so rapid page switches can never duplicate the list.
    /// </summary>
    [RelayCommand]
    public async Task SearchForPythonAsync()
    {
        var generation = ++_searchGeneration;
        IsSearching = true;

        try
        {
            var found = await DiscoverPythonsAsync();
            if (generation != _searchGeneration)
                return;   // a newer search owns the list now

            AvailablePythons.Clear();
            foreach (var installation in found)
            {
                AvailablePythons.Add(installation);
            }

            if (found.Count > 0)
            {
                // Auto-select first one if no path is set
                if (string.IsNullOrEmpty(CustomPythonPath))
                {
                    await SelectPython(found[0]);
                }
            }
            else
            {
                PythonStatus = "✗ No Python with Nazca found";
                NazcaStatus = "Install Nazca from nazca-design.org";
            }
        }
        catch (Exception ex)
        {
            if (generation == _searchGeneration)
            {
                _errorConsole?.LogError($"Python discovery failed: {ex.Message}", ex);
                PythonStatus = $"✗ Search failed: {ex.Message}";
            }
        }
        finally
        {
            if (generation == _searchGeneration)
                IsSearching = false;
        }
    }

    /// <summary>
    /// Manually sets the Python path and updates status.
    /// </summary>
    /// <param name="pythonPath">Path to Python executable.</param>
    public async Task SetPythonPathAsync(string pythonPath)
    {
        CustomPythonPath = pythonPath;
        PythonPathSource = "Custom";
        _exportService.SetCustomPythonPath(pythonPath);
        OnPythonPathChanged?.Invoke(pythonPath);
        await CheckEnvironmentAsync();
    }

    /// <summary>
    /// Selects a discovered Python installation.
    /// </summary>
    /// <param name="installation">The Python installation to use.</param>
    [RelayCommand]
    public async Task SelectPython(PythonDiscoveryService.PythonInstallation installation)
    {
        CustomPythonPath = installation.Path;
        PythonPathSource = installation.Source;
        _exportService.SetCustomPythonPath(installation.Path);
        OnPythonPathChanged?.Invoke(installation.Path);
        await CheckEnvironmentAsync();
    }

}
