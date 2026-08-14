using System.Globalization;
using System.Linq;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.Localization;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_Core.Routing.RerouteImported;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Canvas.RerouteImported;

/// <summary>
/// ViewModel for re-routing imported (frozen) waveguide routes on demand.
/// Counts the design's frozen imported routes, offers "re-route all" / "re-route
/// selected" as ONE undoable command, and reports the before/after length and bend
/// delta so the user sees what the router changed. Hand-edited frozen routes are
/// never re-routed; their count is surfaced instead. Frozen routes living inside
/// component groups (the standard GDS import groups everything it placed) are
/// counted and re-routed in place without dissolving the group.
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

    /// <summary>Re-routable frozen routes living inside component groups.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGroupedFrozenRoutes))]
    [NotifyPropertyChangedFor(nameof(GroupedFrozenText))]
    [NotifyPropertyChangedFor(nameof(IsPanelVisible))]
    private int _groupedFrozenCount;

    /// <summary>Localized "N frozen imported route(s)" line for the panel.</summary>
    public string FrozenCountText => string.Format(CultureInfo.CurrentCulture,
        LocalizationService.Instance.Translate("Routing.Reroute.FrozenCount"), FrozenImportedCount);

    /// <summary>Localized "N hand-edited route(s) kept unchanged" line for the panel.</summary>
    public string HandEditedKeptText => string.Format(CultureInfo.CurrentCulture,
        LocalizationService.Instance.Translate("Routing.Reroute.HandEditedKept"), HandEditedFrozenCount);

    /// <summary>Localized "N frozen route(s) inside groups" line for the panel.</summary>
    public string GroupedFrozenText => string.Format(CultureInfo.CurrentCulture,
        LocalizationService.Instance.Translate("Routing.Reroute.InGroups"), GroupedFrozenCount);

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

    /// <summary>True when re-routable frozen routes live inside component groups.</summary>
    public bool HasGroupedFrozenRoutes => GroupedFrozenCount > 0;

    /// <summary>
    /// Panel visibility: shown while frozen imported routes exist (on the canvas or
    /// inside groups) AND kept visible after a full re-route so the before/after
    /// delta and the "kept unchanged" note stay readable.
    /// </summary>
    public bool IsPanelVisible =>
        HasFrozenImportedRoutes || HasHandEditedFrozenRoutes || HasGroupedFrozenRoutes
        || ResultText.Length > 0;

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
        // Grouping (the standard GDS import ends with one) moves frozen routes off the
        // canvas into the group — the Components collection is where that shows up.
        _canvas.Components.CollectionChanged += (_, _) => Refresh();
        // Routing passes flip IsRouteFrozen outside commands (e.g. an endpoint moved
        // unfreezes the route); StateChanged fires after every pass.
        _canvas.Routing.StateChanged += Refresh;
        _commandManager.StateChanged += (_, _) => Refresh();
    }

    /// <summary>
    /// Recounts eligible and hand-edited frozen routes from the canvas plus the
    /// re-routable frozen paths inside component groups. An emptied canvas (new or
    /// switched design) also discards the last re-route delta.
    /// </summary>
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
        GroupedFrozenCount = _canvas.Components
            .Select(c => c.Component)
            .OfType<ComponentGroup>()
            .Sum(ImportedRouteRerouteEligibility.CountEligibleGroupInternal);
        if (_canvas.Components.Count == 0 && _canvas.Connections.Count == 0)
            ResultText = "";
        RerouteSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Re-routes every eligible frozen imported route in the design.</summary>
    [RelayCommand(CanExecute = nameof(CanRerouteAll))]
    private async Task RerouteAll()
    {
        var canvasTargets = _canvas.Connections
            .Where(c => ImportedRouteRerouteEligibility.IsEligible(c.Connection))
            .ToList();
        var groupTargets = CollectGroupTargets(_canvas).ToList();

        if (canvasTargets.Count == 0 && groupTargets.Count == 0)
            return;

        await RerouteAllAsync(canvasTargets, groupTargets);
    }

    private bool CanRerouteAll() =>
        !IsRerouting && (FrozenImportedCount > 0 || GroupedFrozenCount > 0);

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

    private async Task RerouteAllAsync(
        IReadOnlyList<WaveguideConnectionViewModel> canvasTargets,
        IReadOnlyList<(ComponentGroup Group, List<FrozenWaveguidePath> Paths)> groupTargets)
    {
        IsRerouting = true;
        try
        {
            var allConnectionTargets = canvasTargets.Select(t => t.Connection).ToList();
            var allGroupPaths = groupTargets.SelectMany(g => g.Paths).ToList();
            var before = RouteMetricsSnapshot.Capture(allConnectionTargets, allGroupPaths);

            var connectionStates = canvasTargets.Select(t =>
                new RerouteImportedRoutesStateCommand.ConnectionState
                {
                    Connection = t.Connection,
                    OldPath = t.Connection.RoutedPath!.DeepCopy(),
                    OldIsFrozen = t.Connection.IsRouteFrozen
                }).ToList();

            var groupStates = groupTargets.Select(g =>
                new RerouteImportedRoutesStateCommand.GroupState
                {
                    Group = g.Group,
                    OldPaths = g.Group.InternalPaths
                        .Select(RerouteImportedRoutesStateCommand.CloneWithPins)
                        .ToList()
                }).ToList();

            // Canvas-level targets are mutated directly; the state command handles undo.
            if (canvasTargets.Count > 0)
            {
                new RerouteImportedRoutesCommand(_canvas, canvasTargets).Execute();
                await _canvas.RecalculateRoutesAsync();
            }

            // Group-internal targets are routed inside a temporary group-edit sub-canvas.
            foreach (var (group, paths) in groupTargets)
                await RerouteGroupInternalAsync(group, paths);

            foreach (var state in connectionStates)
            {
                state.NewPath = state.Connection.RoutedPath!.DeepCopy();
                state.NewIsFrozen = state.Connection.IsRouteFrozen;
            }

            foreach (var state in groupStates)
            {
                state.NewPaths = state.Group.InternalPaths
                    .Select(RerouteImportedRoutesStateCommand.CloneWithPins)
                    .ToList();
            }

            var stateCommand = new RerouteImportedRoutesStateCommand(
                _canvas, connectionStates, groupStates);
            _commandManager.ExecuteCommand(stateCommand);
            await _canvas.RecalculateRoutesAsync();

            var afterConnections = connectionStates.Select(s => s.Connection);
            var afterGroupPaths = groupStates.SelectMany(s => s.NewPaths);
            var after = RouteMetricsSnapshot.Capture(afterConnections, afterGroupPaths);
            ResultText = FormatDelta(
                canvasTargets.Count + groupTargets.Sum(g => g.Paths.Count), before, after);
        }
        finally
        {
            IsRerouting = false;
            Refresh();
        }
    }

    private async Task RerouteGroupInternalAsync(
        ComponentGroup group, IReadOnlyList<FrozenWaveguidePath> paths)
    {
        _canvas.EnterGroupEditMode(group);
        try
        {
            var targets = _canvas.Connections
                .Where(vm => paths.Any(p =>
                    ReferenceEquals(vm.Connection.StartPin, p.StartPin) &&
                    ReferenceEquals(vm.Connection.EndPin, p.EndPin)))
                .ToList();

            if (targets.Count > 0)
                new RerouteImportedRoutesCommand(_canvas, targets).Execute();

            await _canvas.RecalculateRoutesAsync();
        }
        finally
        {
            _canvas.ExitGroupEditMode();
        }
    }

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

    private static IEnumerable<(ComponentGroup Group, List<FrozenWaveguidePath> Paths)> CollectGroupTargets(
        DesignCanvasViewModel canvas)
    {
        return canvas.Components
            .Select(c => c.Component)
            .OfType<ComponentGroup>()
            .SelectMany(CollectGroupTargetsRecursive);
    }

    private static IEnumerable<(ComponentGroup Group, List<FrozenWaveguidePath> Paths)> CollectGroupTargetsRecursive(
        ComponentGroup group)
    {
        var direct = group.InternalPaths
            .Where(ImportedRouteRerouteEligibility.IsEligibleGroupInternal)
            .ToList();
        if (direct.Count > 0)
            yield return (group, direct);

        foreach (var childGroup in group.ChildComponents.OfType<ComponentGroup>())
        {
            foreach (var nested in CollectGroupTargetsRecursive(childGroup))
                yield return nested;
        }
    }
}
