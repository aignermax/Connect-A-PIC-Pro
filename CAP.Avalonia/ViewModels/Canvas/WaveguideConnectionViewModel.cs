using CommunityToolkit.Mvvm.ComponentModel;
using CAP_Core.Components.Connections;

namespace CAP.Avalonia.ViewModels.Canvas;

public partial class WaveguideConnectionViewModel : ObservableObject
{
    public WaveguideConnection Connection { get; }

    [ObservableProperty] private bool _isSelected;

    public double StartX => Connection.StartPin.GetAbsolutePosition().x;
    public double StartY => Connection.StartPin.GetAbsolutePosition().y;
    public double EndX => Connection.EndPin.GetAbsolutePosition().x;
    public double EndY => Connection.EndPin.GetAbsolutePosition().y;
    public double PathLength => Connection.PathLengthMicrometers;
    public double LossDb => Connection.TotalLossDb;
    public bool IsBlockedFallback => Connection.IsBlockedFallback;

    public WaveguideConnectionViewModel(WaveguideConnection connection)
    {
        Connection = connection;
    }

    public void NotifyPathChanged()
    {
        OnPropertyChanged(nameof(StartX));
        OnPropertyChanged(nameof(StartY));
        OnPropertyChanged(nameof(EndX));
        OnPropertyChanged(nameof(EndY));
        OnPropertyChanged(nameof(PathLength));
        OnPropertyChanged(nameof(LossDb));
        OnPropertyChanged(nameof(IsBlockedFallback));
    }
}
