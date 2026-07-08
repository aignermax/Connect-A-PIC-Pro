using CAP_Core.Export;
using CAP_Core.Export.PythonEnvironmentManager;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Export.PythonEnvironmentManager;

/// <summary>
/// One row in the unified interpreter list on the Python Environments tab (issue #645):
/// either a managed environment or a discovered system Python. Both are activated the same
/// way; managed entries additionally expose a health check and removal. Managed rows show a
/// live status badge and refresh as an install progresses.
/// </summary>
public partial class InterpreterEntryViewModel : ObservableObject
{
    private readonly PythonEnvironment? _env;
    private readonly string? _systemDisplayText;

    /// <summary>Full path to the interpreter executable.</summary>
    public string Path { get; }

    /// <summary>True when this row is a managed environment (create/remove/health apply).</summary>
    public bool IsManaged => _env != null;

    /// <summary>Registry name for managed rows; null for discovered system Pythons.</summary>
    public string? ManagedName => _env?.Name;

    /// <summary>True when this interpreter is the currently active one.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>Creates a row for a managed environment.</summary>
    /// <param name="env">The managed environment model.</param>
    /// <param name="isActive">Whether this is the active interpreter.</param>
    public InterpreterEntryViewModel(PythonEnvironment env, bool isActive)
    {
        _env = env;
        Path = env.PythonExecutable;
        IsActive = isActive;
    }

    /// <summary>Creates a row for a discovered system interpreter.</summary>
    /// <param name="install">The discovered installation.</param>
    /// <param name="isActive">Whether this is the active interpreter.</param>
    public InterpreterEntryViewModel(PythonDiscoveryService.PythonInstallation install, bool isActive)
    {
        _systemDisplayText = install.DisplayText;
        Path = install.Path;
        IsActive = isActive;
    }

    /// <summary>
    /// Primary label. Managed rows always spell out Nazca and gdsfactory (incl. "not
    /// installed") so a Nazca-only environment is distinguishable from a gdsfactory-capable
    /// one at a glance; system rows reuse the discovery display text.
    /// </summary>
    public string DisplayText => _env != null ? BuildManagedText(_env) : _systemDisplayText!;

    /// <summary>True when a status badge should be shown (managed rows only).</summary>
    public bool HasStatusBadge => IsManaged;

    /// <summary>Short status badge for managed rows, e.g. "Healthy", "Installing…".</summary>
    public string StatusBadge => _env?.Status switch
    {
        PythonEnvironmentStatus.Healthy    => "Healthy",
        PythonEnvironmentStatus.Broken     => "Broken",
        PythonEnvironmentStatus.Creating   => "Creating…",
        PythonEnvironmentStatus.Installing => "Installing…",
        null                               => string.Empty,
        _                                  => "Unknown",
    };

    /// <summary>Badge foreground colour as a CSS-style hex string.</summary>
    public string StatusColor => _env?.Status switch
    {
        PythonEnvironmentStatus.Healthy    => "#88CC88",
        PythonEnvironmentStatus.Broken     => "#CC6666",
        PythonEnvironmentStatus.Creating   => "#CCCC66",
        PythonEnvironmentStatus.Installing => "#66AACC",
        _                                  => "#888888",
    };

    /// <summary>Notifies the UI that the computed managed properties may have changed.</summary>
    public void RefreshAll()
    {
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(StatusBadge));
        OnPropertyChanged(nameof(StatusColor));
    }

    private static string BuildManagedText(PythonEnvironment e)
    {
        var py = e.PythonVersion != null ? $"Python {e.PythonVersion}" : "Python ?";
        var nazca = e.NazcaVersion != null ? $"Nazca {e.NazcaVersion}" : "Nazca not installed";
        var gds = e.GdsFactoryVersion != null ? $"gdsfactory {e.GdsFactoryVersion}" : "gdsfactory not installed";
        return $"Managed · {e.Name} · {py} · {nazca} · {gds}";
    }
}
