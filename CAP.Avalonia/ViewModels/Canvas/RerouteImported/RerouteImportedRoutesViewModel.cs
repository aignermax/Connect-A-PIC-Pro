using System.Globalization;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.Localization;
using CAP_Core.Routing.RerouteImported;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Canvas.RerouteImported;

/// <summary>
/// ViewModel for re-routing imported (frozen) waveguide routes on demand (issue #857).
/// Counts the design's frozen imported routes, offers "re-route all" / "re-route
/// selected" as ONE undoable command, and reports the before/after length and bend
/// delta so the user sees what the router changed. Hand-edited frozen routes are
/// never re-routed; their count is surfaced instead.
/// </summary>
public partial class RerouteImportedRoutesViewModel : ObservableObject
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly CommandManager _commandManager;

    /// <summary>Number of frozen imported routes the router may replace.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFrozenImportedRoutes))]
    [NotifyPropertyChangedFor(nameof(FrozenCountText))]
    [NotifyPropertyChangedFor(nameof(IsPanelVisible))]
    [NotifyCanExecuteChangedFor(nameof(RerouteAllCommand))]
    private int _frozenImportedCount;

    /// <summary>Frozen routes kept unchanged because they carry manual edits.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHandEditedFrozenRoutes))]
    [NotifyPropertyChangedFor(nameof(HandEditedKeptText))]
    [NotifyPropertyChangedFor(nameof(IsPanelVisible))]
    private int _handEditedFrozenCount;

    /// <summary>Localized "N frozen imported route(s)" line for the panel.</summary>
    public string FrozenCountText => string.Format(CultureInfo.CurrentCulture,
        LocalizationService.Instance.Translate("Routing.Reroute.FrozenCount"), FrozenImportedCount);

    /// <summary>Localized "N hand-edited route(s) kept unchanged" line for the panel.</summary>
    public string HandEditedKeptText => string.Format(CultureInfo.CurrentCulture,
        LocalizationService.Instance.Translate("Routing.Reroute.HandEditedKept"), HandEditedFrozenCount);

    /// <summary>Before/after summary of the last re-route pass, empty until one ran.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelVisible))]
    private string _resultText = "";

    /// <summary>True while a re-route pass is running (buttons disable).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RerouteAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RerouteSelectedCommand))]
    private bool _isRerouting;

    /// <summary>True when the design contains at least one re-routable imported route.</summary>
    public bool HasFrozenImportedRoutes => FrozenImportedCount > 0;

    /// <summary>True when hand-edited frozen routes exist (shown as "kept unchanged").</summary>
    public bool HasHandEditedFrozenRoutes => HandEditedFrozenCount > 0;

    /// <summary>
    /// Panel visibility: shown while frozen imported routes exist AND kept visible after a
    /// full re-route so the before/after delta and the "kept unchanged" note stay readable.
    /// </summary>
    public bool IsPanelVisible =>
        HasFrozenImportedRoutes || HasHandEditedFrozenRoutes || ResultText.Length > 0;

    private WaveguideConnectionViewModel? _selectedConnection;

    /// <summary>The currently selected connection, fed by <c>MainViewModel</c>.</summary>
    public WaveguideConnectionViewModel? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            if (SetProperty(ref _selectedConnection, value))
                RerouteSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Initializes the ViewModel and tracks connection/undo state changes.</summary>
    public RerouteImportedRoutesViewModel(DesignCanvasViewModel canvas, CommandManager commandManager)
    {
        _canvas = canvas;
        _commandManager = commandManager;
        _canvas.Connections.CollectionChanged += (_, _) => Refresh();
        _commandManager.StateChanged += (_, _) => Refresh();
    }

    /// <summary>Recounts eligible and hand-edited frozen routes from the canvas.</summary>
    public void Refresh()
    {
        var eligible = 0;
        var handEdited = 0;
        foreach (var connection in _canvas.Connections)
        {
            if (ImportedRouteRerouteEligibility.IsEligible(connection.Connection))
                eligible++;
            else if (ImportedRouteRerouteEligibility.IsKeptHandEdited(connection.Connection))
                handEdited++;
        }
        FrozenImportedCount = eligible;
        HandEditedFrozenCount = handEdited;
        RerouteSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Re-routes every eligible frozen imported route in the design.</summary>
    [RelayCommand(CanExecute = nameof(CanRerouteAll))]
    private async Task RerouteAll()
    {
        var targets = _canvas.Connections
            .Where(c => ImportedRouteRerouteEligibility.IsEligible(c.Connection))
            .ToList();
        await RerouteAsync(targets);
    }

    private bool CanRerouteAll() => !IsRerouting && FrozenImportedCount > 0;

    /// <summary>Re-routes only the selected connection.</summary>
    [RelayCommand(CanExecute = nameof(CanRerouteSelected))]
    private async Task RerouteSelected()
    {
        if (SelectedConnection is { } selected)
            await RerouteAsync(new List<WaveguideConnectionViewModel> { selected });
    }

    private bool CanRerouteSelected() =>
        !IsRerouting
        && SelectedConnection is { } selected
        && ImportedRouteRerouteEligibility.IsEligible(selected.Connection);

    private async Task RerouteAsync(IReadOnlyList<WaveguideConnectionViewModel> targets)
    {
        if (targets.Count == 0)
            return;

        IsRerouting = true;
        try
        {
            var connections = targets.Select(t => t.Connection).ToList();
            var before = RouteMetricsSnapshot.Capture(connections);

            _commandManager.ExecuteCommand(new RerouteImportedRoutesCommand(_canvas, targets));
            // Execute fires the pass asynchronously (undo/redo path); awaiting a second
            // pass here supersedes it and yields a deterministic "after" state.
            await _canvas.RecalculateRoutesAsync();

            var after = RouteMetricsSnapshot.Capture(connections);
            ResultText = FormatDelta(targets.Count, before, after);
        }
        finally
        {
            IsRerouting = false;
            Refresh();
        }
    }

    private static string FormatDelta(int count, RouteMetricsSnapshot before, RouteMetricsSnapshot after) =>
        string.Format(CultureInfo.CurrentCulture,
            LocalizationService.Instance.Translate("Routing.Reroute.Result"),
            count,
            before.LengthMicrometers, after.LengthMicrometers,
            before.EquivalentBends, after.EquivalentBends);
}
