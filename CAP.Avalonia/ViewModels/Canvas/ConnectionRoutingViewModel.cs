using System.Collections.ObjectModel;
using CAP_Core.Components.Connections;
using CAP_Core.Routing.InterconnectRouting;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Canvas;

/// <summary>
/// ViewModel for per-connection routing options (issue #574): routing style
/// (Nazca primitive), width/bend radius, route freezing and manual per-bend
/// radius overrides.
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

    /// <summary>Waveguide width in micrometers for this connection.</summary>
    [ObservableProperty]
    private double _widthMicrometers = InterconnectSettings.DefaultWidthMicrometers;

    /// <summary>Bend radius in micrometers for this connection.</summary>
    [ObservableProperty]
    private double _bendRadiusMicrometers;

    /// <summary>Whether the routed path is frozen (kept as-is during re-routing).</summary>
    [ObservableProperty]
    private bool _isRouteFrozen;

    /// <summary>Number of bends in the current routed path.</summary>
    [ObservableProperty]
    private int _bendCount;

    /// <summary>1-based index of the bend to edit.</summary>
    [ObservableProperty]
    private int _bendNumber = 1;

    /// <summary>Manual radius in micrometers for the selected bend.</summary>
    [ObservableProperty]
    private double _bendOverrideRadiusMicrometers = 10.0;

    /// <summary>Feedback for the last bend edit attempt.</summary>
    [ObservableProperty]
    private string _statusText = "";

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
            StatusText = "";
            var conn = SelectedConnection?.Connection;
            SelectedStyle = conn?.Type ?? WaveguideType.Auto;
            WidthMicrometers = conn?.WidthMicrometers ?? InterconnectSettings.DefaultWidthMicrometers;
            BendRadiusMicrometers = conn?.BendRadiusMicrometers ?? 0;
            IsRouteFrozen = conn?.IsRouteFrozen ?? false;
            RefreshBendInfo();
        }
        finally
        {
            _isUpdatingFromModel = false;
        }
    }

    private void RefreshBendInfo()
    {
        var segments = SelectedConnection?.Connection.GetPathSegments();
        BendCount = segments == null ? 0 : BendRadiusEditor.CountBends(segments);
        if (BendNumber > BendCount) BendNumber = Math.Max(1, BendCount);
    }

    partial void OnSelectedStyleChanged(WaveguideType value) =>
        ApplyToConnection(conn => conn.Type = value);

    partial void OnWidthMicrometersChanged(double value) =>
        ApplyToConnection(conn => conn.WidthMicrometers = value);

    partial void OnBendRadiusMicrometersChanged(double value) =>
        ApplyToConnection(conn =>
        {
            conn.BendRadiusMicrometers = value;
            TriggerReroute(conn);
        });

    partial void OnIsRouteFrozenChanged(bool value) =>
        ApplyToConnection(conn =>
        {
            conn.IsRouteFrozen = value;
            if (!value)
            {
                // Unfreezing discards manual bend edits and re-routes from scratch.
                conn.BendRadiusOverrides.Clear();
                TriggerReroute(conn);
            }
        });

    private void ApplyToConnection(Action<WaveguideConnection> apply)
    {
        if (_isUpdatingFromModel || SelectedConnection == null)
            return;
        apply(SelectedConnection.Connection);
        SelectedConnection.NotifyPathChanged();
    }

    private void TriggerReroute(WaveguideConnection conn)
    {
        if (!conn.IsRouteFrozen)
            _ = _canvas.RecalculateRoutesAsync();
    }

    /// <summary>Applies the manual radius to the selected bend and freezes the route.</summary>
    [RelayCommand]
    private void ApplyBendRadius()
    {
        if (SelectedConnection == null)
            return;

        var conn = SelectedConnection.Connection;
        if (BendRadiusEditor.TryApplyOverride(conn, BendNumber - 1, BendOverrideRadiusMicrometers, out var error))
        {
            _isUpdatingFromModel = true;
            IsRouteFrozen = conn.IsRouteFrozen;
            _isUpdatingFromModel = false;
            StatusText = $"Bend {BendNumber} set to {BendOverrideRadiusMicrometers:F1} µm (route frozen).";
            SelectedConnection.NotifyPathChanged();
        }
        else
        {
            StatusText = error ?? "Bend radius could not be applied.";
        }
    }
}
