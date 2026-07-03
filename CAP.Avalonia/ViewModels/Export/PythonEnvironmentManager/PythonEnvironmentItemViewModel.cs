using CAP_Core.Export.PythonEnvironmentManager;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Export.PythonEnvironmentManager;

/// <summary>
/// Observable wrapper for a single <see cref="PythonEnvironment"/>.
/// Provides bindable status badge text and colour for the list in the manager panel.
/// </summary>
public partial class PythonEnvironmentItemViewModel : ObservableObject
{
    /// <summary>The underlying environment model.</summary>
    public PythonEnvironment Environment { get; }

    [ObservableProperty]
    private bool _isActive;

    /// <summary>Display name of the environment.</summary>
    public string Name => Environment.Name;

    /// <summary>Short status badge, e.g. "Healthy", "Broken", "Installing…".</summary>
    public string StatusBadge => Environment.Status switch
    {
        PythonEnvironmentStatus.Healthy   => "Healthy",
        PythonEnvironmentStatus.Broken    => "Broken",
        PythonEnvironmentStatus.Creating  => "Creating…",
        PythonEnvironmentStatus.Installing => "Installing…",
        _                                  => "Unknown",
    };

    /// <summary>Badge foreground colour as a CSS-style hex string.</summary>
    public string StatusColor => Environment.Status switch
    {
        PythonEnvironmentStatus.Healthy    => "#88CC88",
        PythonEnvironmentStatus.Broken     => "#CC6666",
        PythonEnvironmentStatus.Creating   => "#CCCC66",
        PythonEnvironmentStatus.Installing => "#66AACC",
        _                                  => "#888888",
    };

    /// <summary>
    /// Detail line shown below the name: Python version, Nazca version, pyclipper presence.
    /// </summary>
    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (Environment.PythonVersion != null)
                parts.Add($"Python {Environment.PythonVersion}");
            if (Environment.NazcaVersion != null)
                parts.Add($"Nazca {Environment.NazcaVersion}");
            if (Environment.HasPyclipper)
                parts.Add("pyclipper ✓");
            // gdsfactory is always surfaced (incl. "not installed") so the user can tell a
            // Nazca-only environment from a gdsfactory-capable one at a glance (issue #645).
            parts.Add(Environment.GdsFactoryVersion != null
                ? $"gdsfactory {Environment.GdsFactoryVersion}"
                : "gdsfactory not installed");
            if (!string.IsNullOrEmpty(Environment.LastError))
                parts.Add($"Error: {Environment.LastError}");
            return parts.Count > 0 ? string.Join("  |  ", parts) : string.Empty;
        }
    }

    /// <summary>Initialises the item ViewModel from the given model.</summary>
    /// <param name="environment">The environment model to wrap.</param>
    public PythonEnvironmentItemViewModel(PythonEnvironment environment)
    {
        Environment = environment;
    }

    /// <summary>Notifies the UI that all computed properties may have changed.</summary>
    public void RefreshAll()
    {
        OnPropertyChanged(nameof(StatusBadge));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(Name));
    }
}
