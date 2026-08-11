using System.Collections.ObjectModel;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.Localization;
using CAP_Core.Components.Connections;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Canvas;

/// <summary>
/// ViewModel for per-connection routing: the user picks only the routing style
/// (<see cref="WaveguideType"/>). A non-Auto style reshapes the visible canvas curve into the
/// matching primitive geometry (see <c>ConnectionStyleRouteBuilder</c>); width and bend radius
/// are applied automatically from the interconnect defaults — there is no manual number UI.
/// Auto restores the collision-avoiding A* route.
/// When the rubber-band selection contains multiple connections (issue #862), the style applies
/// to all of them as ONE undoable command.
/// </summary>
public partial class ConnectionRoutingViewModel : ObservableObject
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly CommandManager? _commandManager;
    private bool _isUpdatingFromModel;

    /// <summary>Available routing styles, in display order.</summary>
    public ObservableCollection<WaveguideType> RoutingStyles { get; } =
        new(Enum.GetValues<WaveguideType>());

    private WaveguideConnectionViewModel? _selectedConnection;

    /// <summary>The currently selected connection (single-click selection).</summary>
    public WaveguideConnectionViewModel? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            if (SetProperty(ref _selectedConnection, value))
                UpdateFromSelection();
        }
    }

    /// <summary>Selected routing style; applied to the selected connection(s) immediately.</summary>
    [ObservableProperty]
    private WaveguideType _selectedStyle = WaveguideType.Auto;

    /// <summary>Names the effective geometry of a direct-styled Auto route ("" when not applicable).</summary>
    [ObservableProperty]
    private string _effectiveStyleText = "";

    /// <summary>Number of optical connections the style control currently applies to.</summary>
    [ObservableProperty]
    private int _targetConnectionCount;

    /// <summary>True when the style control targets more than one connection.</summary>
    public bool IsBatchSelection => TargetConnectionCount > 1;

    /// <summary>Localized hint shown above the style dropdown for a multi-selection.</summary>
    public string BatchHint => string.Format(
        LocalizationService.Instance.Translate("Routing.Connection.BatchApply"),
        TargetConnectionCount);

    /// <summary>Initializes a new instance bound to the design canvas.</summary>
    /// <param name="canvas">The design canvas providing the selection and route recalculation.</param>
    /// <param name="commandManager">
    /// Undo/redo manager; when provided, style changes are recorded as one undoable command.
    /// </param>
    public ConnectionRoutingViewModel(DesignCanvasViewModel canvas, CommandManager? commandManager = null)
    {
        _canvas = canvas;
        _commandManager = commandManager;
        // Never unsubscribed: although registered transient, this VM is only ever resolved into
        // the singleton BottomPanelViewModel, so it lives as long as the canvas it observes.
        // If it ever becomes truly transient, add IDisposable and detach this handler.
        _canvas.Selection.SelectedConnections.CollectionChanged += (_, _) => UpdateFromSelection();
    }

    partial void OnTargetConnectionCountChanged(int value)
    {
        OnPropertyChanged(nameof(IsBatchSelection));
        OnPropertyChanged(nameof(BatchHint));
    }

    /// <summary>
    /// The optical connections a style change applies to: the multi-selection when it holds
    /// any connections, otherwise the single clicked connection. Electrical connections are
    /// metal traces (#682) and never take a routing style (revisit with #854).
    /// </summary>
    private List<WaveguideConnectionViewModel> TargetConnections()
    {
        var selected = _canvas.Selection.SelectedConnections
            .Where(c => !c.Connection.IsElectrical)
            .ToList();
        if (selected.Count > 0)
            return selected;
        if (SelectedConnection is { } single && !single.Connection.IsElectrical)
            return new List<WaveguideConnectionViewModel> { single };
        return new List<WaveguideConnectionViewModel>();
    }

    private void UpdateFromSelection()
    {
        var targets = TargetConnections();
        TargetConnectionCount = targets.Count;

        _isUpdatingFromModel = true;
        try
        {
            // Mirror the first target's style (all show the same value when uniform); the
            // dropdown then restyles the whole batch when the user picks a different entry.
            SelectedStyle = targets.Count > 0 ? targets[0].Connection.Type : WaveguideType.Auto;
            EffectiveStyleText = ComputeEffectiveStyleText(targets);
        }
        finally
        {
            _isUpdatingFromModel = false;
        }
    }

    /// <summary>
    /// Names the effective geometry of direct-styled Auto routes. For a multi-selection the
    /// text is shown only when every target resolved to the same direct style — a mixed batch
    /// has no single truthful label, so the field stays empty and the batch hint takes over.
    /// </summary>
    private static string ComputeEffectiveStyleText(List<WaveguideConnectionViewModel> targets)
    {
        var styles = targets
            .Select(t => t.Connection.RoutedPath is { IsDirectStyledRoute: true, DirectStyle: { } style }
                ? style
                : (WaveguideType?)null)
            .Distinct()
            .ToList();
        if (styles.Count != 1 || styles[0] is not { } uniform)
            return "";
        return string.Format(
            LocalizationService.Instance.Translate("Routing.Connection.EffectiveStyleFormat"),
            uniform);
    }

    partial void OnSelectedStyleChanged(WaveguideType value)
    {
        if (_isUpdatingFromModel)
            return;

        var targets = TargetConnections();
        if (targets.Count == 0)
            return;

        // ONE command for the whole batch, so a single Ctrl+Z restores every connection.
        // Width/radius stay each connection's own values (its model defaults) — deliberately
        // NOT stamped with the InterconnectSettings export defaults: their 50 µm bend radius
        // is a Nazca header constant, and writing it onto the connection made the A* route
        // unusably wide after switching back to Auto (it feeds the router's minimum).
        var command = new ChangeRoutingStyleCommand(_canvas, targets, value);
        if (_commandManager != null)
            _commandManager.ExecuteCommand(command);
        else
            command.Execute();
    }
}
