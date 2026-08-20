using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

namespace CAP.Avalonia.Views.Panels;

/// <summary>
/// Collapsible right-panel section that assembles the logic network of the loaded
/// design and shows live gate outputs while the user toggles the network inputs.
/// DataContext is inherited from MainWindow (MainViewModel). The ViewModel stays
/// timer-free so tests advance playback ticks synchronously; this code-behind owns
/// the DispatcherTimer that turns <see cref="LogicPanelViewModel.IsPlaying"/> into
/// wall-clock ticks (issue #1069). The Run mode's auto-clock (#1111) lives behind
/// an injected scheduler in the ViewModel; detaching the panel stops it here.
/// </summary>
public partial class LogicPanel : UserControl
{
    private LogicPanelViewModel? _logic;
    private DispatcherTimer? _playbackTimer;

    /// <summary>Initializes the LogicPanel.</summary>
    public LogicPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_logic != null)
            _logic.PropertyChanged -= OnLogicPropertyChanged;
        _logic = (DataContext as MainViewModel)?.RightPanel.Logic;
        if (_logic != null)
            _logic.PropertyChanged += OnLogicPropertyChanged;
        SyncPlaybackTimer();
    }

    private void OnLogicPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogicPanelViewModel.IsPlaying))
            SyncPlaybackTimer();
    }

    /// <summary>Runs the timer while the VM plays; stops it when playback pauses or ends.</summary>
    private void SyncPlaybackTimer()
    {
        if (_logic?.IsPlaying == true)
        {
            if (_playbackTimer == null)
            {
                _playbackTimer = new DispatcherTimer { Interval = LogicPanelViewModel.PlaybackInterval };
                _playbackTimer.Tick += (_, _) => _logic?.AdvancePlaybackTick();
            }
            _playbackTimer.Start();
            return;
        }
        _playbackTimer?.Stop();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _playbackTimer?.Stop();
        _logic?.StopRun();
        base.OnDetachedFromVisualTree(e);
    }
}
