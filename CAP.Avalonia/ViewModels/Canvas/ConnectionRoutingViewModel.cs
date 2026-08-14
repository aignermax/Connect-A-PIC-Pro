using System.Collections.ObjectModel;
using CAP_Core.Components.Connections;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Canvas;

/// <summary>
/// ViewModel for per-connection routing: the user picks only the routing style
/// (<see cref="WaveguideType"/>). A non-Auto style reshapes the visible canvas curve into the
/// matching primitive geometry (see <c>ConnectionStyleRouteBuilder</c>); width and bend radius
/// are applied automatically from the interconnect defaults — there is no manual number UI.
/// Auto restores the collision-avoiding A* route.
/// </summary>
public partial class ConnectionRoutingViewModel : ObservableObject
{
    private readonly DesignCanvasViewModel _canvas;
    private bool _isUpdatingFromModel;

    /// <summary>Available routing styles, in display order.</summary>
    public ObservableCollection<WaveguideType> RoutingStyles { get; } =
        new(Enum.GetValues<WaveguideType>());

    private WaveguideConnectionViewModel? _selectedConnection;

    /// <summary>The currently selected connection.</summary>
    public WaveguideConnectionViewModel? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            if (SetProperty(ref _selectedConnection, value))
                UpdateFromConnection();
        }
    }

    /// <summary>Selected routing style; applied to the connection immediately.</summary>
    [ObservableProperty]
    private WaveguideType _selectedStyle = WaveguideType.Auto;

    /// <summary>Names the effective geometry of a direct-styled Auto route ("" when not applicable).</summary>
    [ObservableProperty]
    private string _effectiveStyleText = "";

    /// <summary>Initializes a new instance bound to the design canvas.</summary>
    public ConnectionRoutingViewModel(DesignCanvasViewModel canvas)
    {
        _canvas = canvas;
    }

    private void UpdateFromConnection()
    {
        _isUpdatingFromModel = true;
        try
        {
            SelectedStyle = SelectedConnection?.Connection.Type ?? WaveguideType.Auto;
            var path = SelectedConnection?.Connection.RoutedPath;
            EffectiveStyleText = path is { IsDirectStyledRoute: true, DirectStyle: { } style }
                ? string.Format(
                    CAP.Avalonia.Services.Localization.LocalizationService.Instance.Translate("Routing.Connection.EffectiveStyleFormat"),
                    style)
                : "";
        }
        finally
        {
            _isUpdatingFromModel = false;
        }
    }

    partial void OnSelectedStyleChanged(WaveguideType value)
    {
        if (_isUpdatingFromModel || SelectedConnection == null)
            return;

        var conn = SelectedConnection.Connection;
        conn.Type = value;
        if (value == WaveguideType.Auto)
        {
            // Back to automatic, collision-avoiding A* routing.
            conn.IsRouteFrozen = false;
        }
        // Width/radius stay the connection's own values (its model defaults) — no manual UI.
        // Deliberately NOT stamped with the InterconnectSettings export defaults: their 50 µm
        // bend radius is a Nazca header constant, and writing it onto the connection made the
        // A* route unusably wide after switching back to Auto (it feeds the router's minimum).

        // Drop the current route so the style change takes effect immediately: incremental
        // routing keeps any route whose endpoints still match, so the stale path would otherwise
        // survive until a component moved. RecalculateRoutesAsync then rebuilds it — the styled
        // primitive for an explicit style, or the A* route for Auto.
        conn.InvalidateRoute();

        _ = _canvas.RecalculateRoutesAsync();
    }
}
