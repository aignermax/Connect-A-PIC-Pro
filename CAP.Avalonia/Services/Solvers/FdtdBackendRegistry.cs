using CAP.Avalonia.Services.Localization;
using CAP_Core.Solvers.Fdtd;

namespace CAP.Avalonia.Services.Solvers;

// Central registry of the FDTD S-matrix backends and the user's persisted choice.
// Flows that recompute an S-matrix resolve their IFdtdSMatrixService through this
// registry rather than binding to one implementation.
public class FdtdBackendRegistry
{
    private readonly IReadOnlyDictionary<FdtdBackendType, IFdtdSMatrixService> _services;
    private readonly UserPreferencesService _preferences;

    public FdtdBackendRegistry(
        IReadOnlyDictionary<FdtdBackendType, IFdtdSMatrixService> services,
        UserPreferencesService preferences)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        if (services.Count == 0)
            throw new ArgumentException("At least one FDTD backend must be registered.", nameof(services));
    }

    public IReadOnlyList<FdtdBackendType> AvailableBackends =>
        _services.Keys.OrderBy(k => (int)k).ToList();

    // Falls back to the first registered backend when the saved value names one
    // that is not registered.
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

    public IFdtdSMatrixService CurrentService => _services[SelectedBackend];

    public IFdtdSMatrixService GetService(FdtdBackendType backend) => _services[backend];

    public static string DisplayName(FdtdBackendType backend) =>
        LocalizationService.Instance.Translate($"FdtdBackend.{backend}.Name");

    public static string Description(FdtdBackendType backend) =>
        LocalizationService.Instance.Translate($"FdtdBackend.{backend}.Description");

    // Short label used in button captions, status texts and override notes.
    public static string SolverLabel(FdtdBackendType backend) =>
        LocalizationService.Instance.Translate($"FdtdBackend.{backend}.Label");
}
