using CAP_Core.Solvers.ModeSolver;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Solvers;

/// <summary>
/// ViewModel for the "Compute Modes for Waveguide" dialog.
/// Drives cross-section input fields, backend selection, and result display.
/// </summary>
public partial class ModeSolverViewModel : ObservableObject
{
    // ── cross-section inputs ────────────────────────────────────────────────

    /// <summary>Waveguide core width in µm.</summary>
    [ObservableProperty]
    private double _width = 0.45;

    /// <summary>Waveguide core height (thickness) in µm.</summary>
    [ObservableProperty]
    private double _height = 0.22;

    /// <summary>Slab height in µm (0 for fully-etched strip).</summary>
    [ObservableProperty]
    private double _slabHeight = 0.0;

    /// <summary>Core refractive index.</summary>
    [ObservableProperty]
    private double _coreIndex = 3.48;

    /// <summary>Cladding refractive index.</summary>
    [ObservableProperty]
    private double _cladIndex = 1.44;

    /// <summary>Wavelength in nm (converted to µm on submission).</summary>
    [ObservableProperty]
    private double _wavelengthNm = 1550;

    /// <summary>Maximum number of modes to compute.</summary>
    [ObservableProperty]
    private int _numModes = 4;

    /// <summary>Selected backend name string (bound to ComboBox).</summary>
    [ObservableProperty]
    private string _selectedBackend = "GdsfactoryModes";

    // ── status / result ─────────────────────────────────────────────────────

    /// <summary>True while a solve is in progress.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSolve))]
    private bool _isSolving;

    /// <summary>Status text shown below the result table.</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>True on the last successful solve (shows result grid).</summary>
    [ObservableProperty]
    private bool _hasResult;

    /// <summary>Raw stderr captured from the last failed solve.</summary>
    [ObservableProperty]
    private string _rawStderr = "";

    /// <summary>True when stderr is non-empty (shows the raw-error expander).</summary>
    [ObservableProperty]
    private bool _hasStderr;

    /// <summary>Mode entries displayed in the results table.</summary>
    public IReadOnlyList<ModeSolverModeEntry> Modes { get; private set; } = Array.Empty<ModeSolverModeEntry>();

    /// <summary>True when a solve can be initiated.</summary>
    public bool CanSolve => !IsSolving;

    // ── available backend names for ComboBox ─────────────────────────────────
    /// <summary>Backend names exposed to the ComboBox.</summary>
    public static IReadOnlyList<string> AvailableBackends { get; } =
        Enum.GetNames<ModeSolverBackend>();

    // ── dependencies ────────────────────────────────────────────────────────

    private readonly IModeSolverService _service;
    private readonly CAP_Core.Solvers.ModeProbe.CrossSectionDefaultsStore? _defaultsStore;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Initialises the ViewModel with its solver service. The optional
    /// <paramref name="defaultsStore"/> records each manually entered cross-section
    /// so the canvas mode probe (issue #691) can fall back to it.
    /// </summary>
    public ModeSolverViewModel(
        IModeSolverService service,
        CAP_Core.Solvers.ModeProbe.CrossSectionDefaultsStore? defaultsStore = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _defaultsStore = defaultsStore;
    }

    // ── commands ─────────────────────────────────────────────────────────────

    /// <summary>Runs the mode solver with the current cross-section inputs.</summary>
    [RelayCommand(CanExecute = nameof(CanSolve))]
    private async Task Solve()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        IsSolving   = true;
        HasResult   = false;
        HasStderr   = false;
        RawStderr   = "";
        StatusText  = "Solving…";

        try
        {
            var request = BuildRequest();
            // Remember the manual entry so the canvas mode probe can fall back to it
            // when the active PDK carries no geometry/material data (issue #691).
            _defaultsStore?.RecordManualEntry(Width, Height, SlabHeight, CoreIndex, CladIndex);
            var result  = await _service.SolveAsync(request, _cts.Token);

            if (result.Success)
            {
                Modes      = result.Modes;
                OnPropertyChanged(nameof(Modes));
                HasResult  = true;
                StatusText = $"Done — {result.Modes.Count} mode(s) using {result.BackendUsed}.";
            }
            else
            {
                HasResult  = false;
                StatusText = result.Error ?? "Solve failed.";

                if (!string.IsNullOrWhiteSpace(result.RawStderr))
                {
                    RawStderr = result.RawStderr;
                    HasStderr = true;
                }

                if (!string.IsNullOrWhiteSpace(result.MissingBackend))
                    StatusText += $"  →  pip install {result.MissingBackend}";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        finally
        {
            IsSolving = false;
        }
    }

    /// <summary>Cancels a running solve.</summary>
    [RelayCommand]
    private void CancelSolve()
    {
        _cts?.Cancel();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private ModeSolverRequest BuildRequest()
    {
        if (!Enum.TryParse<ModeSolverBackend>(SelectedBackend, out var backend))
            backend = ModeSolverBackend.GdsfactoryModes;

        return new ModeSolverRequest
        {
            Width       = Width,
            Height      = Height,
            SlabHeight  = SlabHeight,
            CoreIndex   = CoreIndex,
            CladIndex   = CladIndex,
            Wavelengths = new[] { WavelengthNm / 1000.0 },  // nm → µm
            Backend     = backend,
            NumModes    = NumModes,
        };
    }
}
