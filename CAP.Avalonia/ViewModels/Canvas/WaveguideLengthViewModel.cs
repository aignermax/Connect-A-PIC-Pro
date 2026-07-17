using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CAP.Avalonia.Services.Localization;
using CAP_Core.Components.Connections;

namespace CAP.Avalonia.ViewModels.Canvas;

/// <summary>
/// ViewModel for parameterized waveguide length configuration.
/// Allows users to set target lengths for phase matching in interferometric designs.
/// </summary>
public partial class WaveguideLengthViewModel : ObservableObject
{
    private WaveguideConnectionViewModel? _selectedConnection;

    /// <summary>
    /// The currently selected connection for length configuration.
    /// </summary>
    public WaveguideConnectionViewModel? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            if (SetProperty(ref _selectedConnection, value))
            {
                UpdateFromConnection();
                OnPropertyChanged(nameof(HasConnection));
                OnPropertyChanged(nameof(ConnectionName));
            }
        }
    }

    /// <summary>
    /// Whether a connection is currently selected.
    /// </summary>
    public bool HasConnection => SelectedConnection != null;

    /// <summary>
    /// Display name for the selected connection.
    /// </summary>
    public string ConnectionName
    {
        get
        {
            if (SelectedConnection == null) return LocalizationService.Instance.Translate("Canvas.NoConnectionSelected");
            var conn = SelectedConnection.Connection;
            return $"{conn.StartPin.Name} → {conn.EndPin.Name}";
        }
    }

    [ObservableProperty]
    private bool _isTargetLengthEnabled;

    [ObservableProperty]
    private double _targetLengthMicrometers = 100.0;

    [ObservableProperty]
    private double _toleranceMicrometers = 1.0;

    [ObservableProperty]
    private double _currentLengthMicrometers;

    [ObservableProperty]
    private string _lengthStatusText = "";

    [ObservableProperty]
    private string _lengthStatusColor = "Gray";

    /// <summary>
    /// Updates the ViewModel properties from the selected connection.
    /// </summary>
    private void UpdateFromConnection()
    {
        if (SelectedConnection == null)
        {
            IsTargetLengthEnabled = false;
            TargetLengthMicrometers = 100.0;
            ToleranceMicrometers = 1.0;
            CurrentLengthMicrometers = 0;
            LengthStatusText = "";
            return;
        }

        var conn = SelectedConnection.Connection;
        IsTargetLengthEnabled = conn.IsTargetLengthEnabled;
        TargetLengthMicrometers = conn.TargetLengthMicrometers ?? 100.0;
        ToleranceMicrometers = conn.LengthToleranceMicrometers;
        UpdateLengthStatus();
    }

    /// <summary>
    /// Updates the current length and status text based on the connection's routed path.
    /// </summary>
    public void UpdateLengthStatus()
    {
        if (SelectedConnection == null)
        {
            CurrentLengthMicrometers = 0;
            LengthStatusText = "";
            LengthStatusColor = "Gray";
            return;
        }

        var conn = SelectedConnection.Connection;
        CurrentLengthMicrometers = conn.PathLengthMicrometers;

        if (!conn.IsTargetLengthEnabled || !conn.TargetLengthMicrometers.HasValue)
        {
            LengthStatusText = $"Current: {CurrentLengthMicrometers:F2} µm";
            LengthStatusColor = "White";
            return;
        }

        var diff = conn.LengthDifference ?? 0;
        var isMatched = conn.IsLengthMatched ?? false;

        if (isMatched)
        {
            LengthStatusText = $"✓ Matched ({CurrentLengthMicrometers:F2} µm, Δ={diff:+0.00;-0.00} µm)";
            LengthStatusColor = "LightGreen";
        }
        else if (diff < 0)
        {
            LengthStatusText = $"⚠ Too short ({CurrentLengthMicrometers:F2} µm, needs {-diff:F2} µm more)";
            LengthStatusColor = "Orange";
        }
        else
        {
            LengthStatusText = $"⚠ Too long ({CurrentLengthMicrometers:F2} µm, {diff:F2} µm excess)";
            LengthStatusColor = "Orange";
        }
    }

    /// <summary>
    /// Applies the current ViewModel settings to the selected connection.
    /// </summary>
    [RelayCommand]
    private void ApplyTargetLength()
    {
        if (SelectedConnection == null) return;

        var conn = SelectedConnection.Connection;
        conn.IsTargetLengthEnabled = IsTargetLengthEnabled;
        conn.TargetLengthMicrometers = TargetLengthMicrometers;
        conn.LengthToleranceMicrometers = ToleranceMicrometers;

        UpdateLengthStatus();
        SelectedConnection.NotifyPathChanged();
    }

    /// <summary>
    /// Sets the target length to match the current actual length.
    /// </summary>
    [RelayCommand]
    private void SetTargetToCurrent()
    {
        if (SelectedConnection == null) return;

        TargetLengthMicrometers = CurrentLengthMicrometers;
        ApplyTargetLength();
    }

    /// <summary>
    /// Disables the target length constraint.
    /// </summary>
    [RelayCommand]
    private void DisableTargetLength()
    {
        IsTargetLengthEnabled = false;
        ApplyTargetLength();
    }

    /// <summary>
    /// Enables the target length constraint.
    /// </summary>
    [RelayCommand]
    private void EnableTargetLength()
    {
        IsTargetLengthEnabled = true;
        ApplyTargetLength();
    }
}
