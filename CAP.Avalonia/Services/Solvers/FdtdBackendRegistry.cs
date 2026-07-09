using CAP_Core.Solvers.Fdtd;

namespace CAP.Avalonia.Services.Solvers;

/// <summary>
/// Central registry of the available FDTD S-matrix backends and the user's
/// persisted choice between them. Any flow that recomputes an S-matrix
/// (component settings today, the planned new-component flow later) resolves its
/// <see cref="IFdtdSMatrixService"/> through this registry rather than binding to
/// one implementation, so the backend picker works the same everywhere.
/// </summary>
public class FdtdBackendRegistry
{
    private readonly IReadOnlyDictionary<FdtdBackendType, IFdtdSMatrixService> _services;
    private readonly UserPreferencesService _preferences;

    /// <summary>Initializes the registry.</summary>
    /// <param name="services">One service per registered backend.</param>
    /// <param name="preferences">Preference store the backend choice persists in.</param>
    public FdtdBackendRegistry(
        IReadOnlyDictionary<FdtdBackendType, IFdtdSMatrixService> services,
        UserPreferencesService preferences)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        if (services.Count == 0)
            throw new ArgumentException("At least one FDTD backend must be registered.", nameof(services));
    }

    /// <summary>Backends available for selection, in enum order.</summary>
    public IReadOnlyList<FdtdBackendType> AvailableBackends =>
        _services.Keys.OrderBy(k => (int)k).ToList();

    /// <summary>
    /// The persisted backend choice. Falls back to the first registered backend
    /// when the saved value names a backend that is not registered.
    /// </summary>
    public FdtdBackendType SelectedBackend
    {
        get
        {
            var saved = _preferences.GetFdtdBackend();
            return _services.ContainsKey(saved) ? saved : AvailableBackends[0];
        }
        set
        {
            if (!_services.ContainsKey(value))
                throw new ArgumentOutOfRangeException(nameof(value), $"Backend {value} is not registered.");
            _preferences.SetFdtdBackend(value);
        }
    }

    /// <summary>The service implementing the currently selected backend.</summary>
    public IFdtdSMatrixService CurrentService => _services[SelectedBackend];

    /// <summary>Returns the service for a specific backend.</summary>
    public IFdtdSMatrixService GetService(FdtdBackendType backend) => _services[backend];

    /// <summary>User-facing display name for a backend.</summary>
    public static string DisplayName(FdtdBackendType backend) => backend switch
    {
        FdtdBackendType.MeepDocker => "Meep (local Docker)",
        FdtdBackendType.Tidy3D => "Tidy3D (cloud)",
        _ => backend.ToString(),
    };

    /// <summary>Short solver label used in S-matrix override notes (e.g. "Meep", "Tidy3D").</summary>
    public static string SolverLabel(FdtdBackendType backend) => backend switch
    {
        FdtdBackendType.MeepDocker => "Meep",
        FdtdBackendType.Tidy3D => "Tidy3D",
        _ => backend.ToString(),
    };
}
