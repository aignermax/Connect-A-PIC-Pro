using CAP_Core.Export;
using CAP_Core.Export.PythonEnvironmentManager;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Export.PythonEnvironmentManager;

/// <summary>
/// ViewModel for the Python Environment Manager panel. Creates managed Python venvs
/// (Nazca + gdsfactory pre-installed) and, together with the discovered system Pythons
/// (see the SystemInterpreters partial), presents them as one unified interpreter list —
/// the single place to pick the active interpreter for export and preview (issue #645).
/// All long-running ops are async, report progress, and are cancellable.
/// </summary>
public partial class PythonEnvironmentManagerViewModel : ObservableObject
{
    private readonly PythonEnvironmentRegistry _registry;
    private readonly UvBootstrapper _bootstrapper;
    private readonly NazcaPackageInstaller _installer;
    private readonly EnvironmentHealthChecker _healthChecker;
    private readonly PythonDiscoveryService _discovery;
    private readonly Func<string?> _getActiveInterpreterPath;

    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private string _newEnvironmentName = string.Empty;

    [ObservableProperty]
    private string _pythonVersion = UvBootstrapper.DefaultPythonVersion;

    /// <summary>
    /// Python versions offered in the create-environment dropdown. Limited to versions
    /// with known-good Nazca/pyclipper support (3.14+ currently breaks numpy/nazca);
    /// the default is <see cref="UvBootstrapper.DefaultPythonVersion"/>.
    /// </summary>
    public IReadOnlyList<string> PythonVersionChoices { get; } =
        new[] { "3.10", "3.11", "3.12", "3.13" };

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private bool _canCancel;

    /// <summary>Initialises the ViewModel with core services.</summary>
    /// <param name="registry">Managed-environment registry.</param>
    /// <param name="bootstrapper">uv bootstrapper for venv creation.</param>
    /// <param name="installer">Nazca / gdsfactory package installer.</param>
    /// <param name="healthChecker">Environment health prober.</param>
    /// <param name="discovery">Discovers system Python interpreters (issue #645); null
    /// falls back to a fresh <see cref="PythonDiscoveryService"/>.</param>
    /// <param name="getActiveInterpreterPath">Returns the currently active interpreter path
    /// so discovered system interpreters can be marked active; null treats none as active.</param>
    public PythonEnvironmentManagerViewModel(
        PythonEnvironmentRegistry registry,
        UvBootstrapper bootstrapper,
        NazcaPackageInstaller installer,
        EnvironmentHealthChecker healthChecker,
        PythonDiscoveryService? discovery = null,
        Func<string?>? getActiveInterpreterPath = null)
    {
        _registry = registry;
        _bootstrapper = bootstrapper;
        _installer = installer;
        _healthChecker = healthChecker;
        _discovery = discovery ?? new PythonDiscoveryService();
        _getActiveInterpreterPath = getActiveInterpreterPath ?? (() => null);

        RebuildInterpreters();
    }

    /// <summary>Name of the environment created by the one-click "install Nazca" offers.</summary>
    public const string DefaultEnvironmentName = "nazca";

    /// <summary>
    /// One-click entry point used by the GDS-export fallback and the export guard:
    /// creates the default Nazca environment with default settings. No-ops (with an
    /// explanatory status) when an environment of that name already exists, and does
    /// nothing while another operation runs.
    /// </summary>
    public async Task StartDefaultNazcaInstallAsync()
    {
        if (IsBusy) return;

        if (_registry.Exists(DefaultEnvironmentName))
        {
            ProgressText = $"Environment '{DefaultEnvironmentName}' already exists — "
                + "select it in the list below, or remove it and recreate it if it is broken.";
            return;
        }

        NewEnvironmentName = DefaultEnvironmentName;
        PythonVersion = UvBootstrapper.DefaultPythonVersion;
        await CreateAndInstallCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Creates a new venv and installs the full dependency set — Nazca + pyclipper and
    /// gdsfactory + ubcpdk — so a freshly created environment works for both the Nazca and
    /// the gdsfactory export without a separate install step (issue #645).
    /// </summary>
    [RelayCommand]
    private async Task CreateAndInstallAsync()
    {
        var name = NewEnvironmentName.Trim();
        if (!EnvironmentNaming.IsValidName(name))
        {
            ProgressText = "Please enter a valid environment name "
                + "(letters, digits, '-', '_', '.'; no path characters).";
            return;
        }

        if (!EnvironmentNaming.IsValidPythonVersion(PythonVersion.Trim()))
        {
            ProgressText = "Please enter a plain Python version, e.g. 3.11 or 3.11.4.";
            return;
        }

        if (_registry.Exists(name))
        {
            ProgressText = $"An environment named '{name}' already exists.";
            return;
        }

        var venvPath = Path.Combine(UvBootstrapper.EnvironmentsBaseDir, name);
        var env = new PythonEnvironment { Name = name, VenvPath = venvPath };

        _registry.AddOrUpdate(env);
        RebuildInterpreters();

        await RunLongOperationAsync(async ct =>
        {
            var progress = CreateProgress(env);

            env.Status = PythonEnvironmentStatus.Creating;
            progress.Report("Locating uv...");

            var uvPath = await _bootstrapper.EnsureUvAsync(progress, ct);

            await _bootstrapper.CreateVenvAsync(uvPath, venvPath, PythonVersion.Trim(), progress, ct);

            env.Status = PythonEnvironmentStatus.Installing;
            await _installer.InstallAsync(uvPath, venvPath, progress, ct);
            await _installer.InstallGdsFactoryAsync(uvPath, venvPath, progress, ct);

            await _healthChecker.CheckAsync(env, ct);
            _registry.AddOrUpdate(env);
        }, env, $"Environment '{name}' is ready (Nazca + gdsfactory).");
    }

    /// <summary>
    /// Ensures a managed environment with gdsfactory exists and is active — used by the
    /// gdsfactory export to auto-provision on first use. Installs gdsfactory (+ubcpdk) into
    /// the active managed env, or creates the default environment (Nazca + gdsfactory) when
    /// none exists, then activates it. Reports progress; returns true when gdsfactory is
    /// available afterwards. No-ops to false while another operation runs.
    /// </summary>
    public async Task<bool> EnsureGdsFactoryInstalledAsync(
        IProgress<string> progress, CancellationToken ct)
    {
        if (IsBusy) return false;

        var active = _registry.GetActive();
        var target = active ?? _registry.GetAll().FirstOrDefault();

        IsBusy = true;
        CanCancel = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var token = _cts.Token;
            var uvPath = await _bootstrapper.EnsureUvAsync(progress, token);

            if (target == null)
            {
                var venvPath = Path.Combine(UvBootstrapper.EnvironmentsBaseDir, DefaultEnvironmentName);
                target = new PythonEnvironment { Name = DefaultEnvironmentName, VenvPath = venvPath };
                progress.Report($"Creating managed environment '{DefaultEnvironmentName}'...");
                await _bootstrapper.CreateVenvAsync(uvPath, venvPath, UvBootstrapper.DefaultPythonVersion, progress, token);
                await _installer.InstallAsync(uvPath, venvPath, progress, token);
            }

            await _installer.InstallGdsFactoryAsync(uvPath, target.VenvPath, progress, token);
            await _healthChecker.CheckAsync(target, token);
            _registry.AddOrUpdate(target);
            _registry.SetActive(target.Name);
            RebuildInterpreters();
            return target.GdsFactoryVersion != null;
        }
        catch (Exception ex)
        {
            ProgressText = $"gdsfactory install failed: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
            CanCancel = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Cancels the in-progress long operation.</summary>
    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        ProgressText = "Cancelling…";
    }

    private IProgress<string> CreateProgress(PythonEnvironment env) =>
        new Progress<string>(msg =>
        {
            ProgressText = msg;
            Interpreters.FirstOrDefault(i => i.ManagedName == env.Name)?.RefreshAll();
        });

    private async Task RunLongOperationAsync(
        Func<CancellationToken, Task> operation,
        PythonEnvironment env,
        string successMessage)
    {
        IsBusy = true;
        CanCancel = true;
        _cts = new CancellationTokenSource();
        try
        {
            await operation(_cts.Token);
            _registry.AddOrUpdate(env);
            RebuildInterpreters();
            ProgressText = successMessage;
        }
        catch (OperationCanceledException)
        {
            env.Status = PythonEnvironmentStatus.Broken;
            env.LastError = "Operation was cancelled.";
            _registry.AddOrUpdate(env);
            RebuildInterpreters();
            ProgressText = "Operation cancelled.";
        }
        catch (Exception ex)
        {
            env.Status = PythonEnvironmentStatus.Broken;
            env.LastError = ex.Message;
            _registry.AddOrUpdate(env);
            RebuildInterpreters();
            ProgressText = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            CanCancel = false;
            _cts?.Dispose();
            _cts = null;
        }
    }
}
