using System.Globalization;
using CAP.Avalonia.Services.Localization;
using CAP_Core.Routing.MeanderGeneration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Canvas;

/// <summary>
/// ViewModel for the "Length matching" section of the Properties panel: when exactly one
/// waveguide connection is selected, the user can stretch its route to an exact target
/// length (± tolerance) via a meander (<see cref="ConnectionLengthMatcher"/>) or clear the
/// target again. A matched route stays frozen so later routing passes keep its length;
/// clearing unfreezes it and hands it back to the normal router. Typed matcher failures are
/// mapped to readable localized messages — the raw enum names never reach the UI.
/// </summary>
public partial class LengthMatchingViewModel : ObservableObject
{
    private const double DefaultToleranceMicrometers = 0.1;

    private readonly DesignCanvasViewModel _canvas;
    private readonly ConnectionLengthMatcher _matcher;

    private WaveguideConnectionViewModel? _selectedConnection;

    /// <summary>The currently selected connection (single-click selection), fed by MainViewModel.</summary>
    public WaveguideConnectionViewModel? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            if (SetProperty(ref _selectedConnection, value))
                UpdateFromSelection();
        }
    }

    /// <summary>Actual geometric length (µm) of the target connection's current route.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentLengthText))]
    private double _currentLengthMicrometers;

    /// <summary>Target length input (µm) as typed; defaults to the current route length.</summary>
    [ObservableProperty]
    private string _targetLengthText = "";

    /// <summary>Tolerance input (µm) as typed; defaults to 0.1 µm.</summary>
    [ObservableProperty]
    private string _toleranceText = "0.1";

    /// <summary>Localized status line shown after Apply/Clear, empty while nothing ran.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    [NotifyPropertyChangedFor(nameof(HasSuccessStatus))]
    [NotifyPropertyChangedFor(nameof(HasErrorStatus))]
    private string _statusMessage = "";

    /// <summary>True when <see cref="StatusMessage"/> reports a failure (shown red), false for success (green).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuccessStatus))]
    [NotifyPropertyChangedFor(nameof(HasErrorStatus))]
    private bool _isStatusError;

    /// <summary>True when a status message is present.</summary>
    public bool HasStatus => StatusMessage.Length > 0;

    /// <summary>True when the status reports success.</summary>
    public bool HasSuccessStatus => HasStatus && !IsStatusError;

    /// <summary>True when the status reports an error.</summary>
    public bool HasErrorStatus => HasStatus && IsStatusError;

    /// <summary>
    /// Panel visibility: exactly one connection is targeted — either the single clicked
    /// connection (batch selection empty) or a batch selection holding exactly one.
    /// </summary>
    public bool HasExactlyOneConnection => TargetConnection() != null;

    /// <summary>Formatted current length for the read-only row.</summary>
    public string CurrentLengthText => string.Format(CultureInfo.CurrentCulture,
        LocalizationService.Instance.Translate("Routing.LengthMatch.CurrentLengthFormat"),
        CurrentLengthMicrometers);

    /// <summary>Initializes a new instance bound to the design canvas.</summary>
    /// <param name="canvas">The design canvas providing the selection, obstacles and router.</param>
    public LengthMatchingViewModel(DesignCanvasViewModel canvas)
    {
        _canvas = canvas;
        _matcher = new ConnectionLengthMatcher(canvas.Router);
        // Never unsubscribed: although registered transient, this VM is only ever resolved into
        // the singleton BottomPanelViewModel, so it lives as long as the canvas it observes.
        // If it ever becomes truly transient, add IDisposable and detach this handler.
        _canvas.Selection.SelectedConnections.CollectionChanged += (_, _) => UpdateFromSelection();
    }

    /// <summary>
    /// The connection length matching applies to: the batch selection when it holds exactly
    /// one connection, otherwise the single clicked connection. A multi-connection batch is
    /// deliberately not supported — a meander is a per-route geometry, and stamping one
    /// target onto several different routes would silently mean different lengths.
    /// </summary>
    private WaveguideConnectionViewModel? TargetConnection()
    {
        var batch = _canvas.Selection.SelectedConnections;
        if (batch.Count == 1)
            return batch[0];
        if (batch.Count > 1)
            return null;
        return SelectedConnection;
    }

    private void UpdateFromSelection()
    {
        OnPropertyChanged(nameof(HasExactlyOneConnection));
        StatusMessage = "";
        var target = TargetConnection();
        if (target == null)
            return;

        CurrentLengthMicrometers = target.Connection.PathLengthMicrometers;
        // Pre-fill with the recorded intent when the route was matched before, so the
        // inputs round-trip a saved design; otherwise mirror the current geometry.
        TargetLengthText = (target.Connection.TargetLengthMicrometers ?? CurrentLengthMicrometers)
            .ToString("0.###", CultureInfo.InvariantCulture);
        ToleranceText = (target.Connection.LengthToleranceMicrometers ?? DefaultToleranceMicrometers)
            .ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Stretches the target connection's route to the entered target length.</summary>
    [RelayCommand]
    private void Apply()
    {
        if (TargetConnection() is not { } target)
            return;

        if (!TryParseMicrometers(TargetLengthText, out double targetLength) || targetLength <= 0
            || !TryParseMicrometers(ToleranceText, out double tolerance) || tolerance < 0)
        {
            SetError("Routing.LengthMatch.Error.InvalidInput");
            return;
        }

        var obstacles = _canvas.Components.Select(vm => vm.Component).ToList();
        var result = _matcher.ApplyTargetLength(target.Connection, obstacles, targetLength, tolerance);
        if (!result.IsSuccess)
        {
            SetError(MapFailureKey(result.FailureReason));
            return;
        }

        // The matcher replaced the geometry on the model; the canvas-bound values only
        // refresh when the view-model re-reads them.
        target.NotifyPathChanged();
        CurrentLengthMicrometers = target.Connection.PathLengthMicrometers;
        IsStatusError = false;
        StatusMessage = string.Format(CultureInfo.CurrentCulture,
            LocalizationService.Instance.Translate("Routing.LengthMatch.Success"),
            CurrentLengthMicrometers);
    }

    /// <summary>
    /// Removes the length intent: drops target/tolerance, unfreezes the route and lets the
    /// normal router rebuild it (the stale meander geometry must be discarded first, or the
    /// incremental router would keep it). A connection without a length target is left
    /// untouched: routes frozen for other reasons (e.g. GDS-imported geometry) must not be
    /// unfrozen and re-routed — that would silently discard their path.
    /// </summary>
    [RelayCommand]
    private async Task Clear()
    {
        if (TargetConnection() is not { } target)
            return;

        var connection = target.Connection;
        if (connection.TargetLengthMicrometers is null)
            return;

        connection.TargetLengthMicrometers = null;
        connection.LengthToleranceMicrometers = null;
        connection.IsRouteFrozen = false;
        connection.InvalidateRoute();
        await _canvas.RecalculateRoutesAsync();
        target.NotifyPathChanged();

        CurrentLengthMicrometers = connection.PathLengthMicrometers;
        IsStatusError = false;
        StatusMessage = LocalizationService.Instance.Translate("Routing.LengthMatch.Cleared");
    }

    private void SetError(string localizationKey)
    {
        IsStatusError = true;
        StatusMessage = LocalizationService.Instance.Translate(localizationKey);
    }

    /// <summary>
    /// Maps the typed matcher failure to its localization key. Unknown reasons fall back to
    /// a generic message so a new enum value never surfaces as a raw identifier in the UI.
    /// </summary>
    private static string MapFailureKey(MeanderFailureReason? reason) => reason switch
    {
        MeanderFailureReason.TargetShorterThanDirectPath => "Routing.LengthMatch.Error.TargetShorterThanDirectPath",
        MeanderFailureReason.BoundsTooSmallForMeander => "Routing.LengthMatch.Error.BoundsTooSmallForMeander",
        MeanderFailureReason.EndpointsNotRoutableAtMinRadius => "Routing.LengthMatch.Error.EndpointsNotRoutableAtMinRadius",
        _ => "Routing.LengthMatch.Error.Unknown",
    };

    /// <summary>
    /// Parses a micrometer input: invariant first (the on-disk format), then the UI culture
    /// so e.g. a German decimal comma is accepted when typing.
    /// </summary>
    private static bool TryParseMicrometers(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }
}
