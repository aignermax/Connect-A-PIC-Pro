using CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.Waveform;
using CAP_Core.Analysis.LogicAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis;

/// <summary>
/// Waveform half of <see cref="LogicPanelViewModel"/> (issue #1129, rung 5
/// visualizer): keeps the waveform strip's display model in sync with the event
/// timeline. The model is a pure function of data the panel already holds — the
/// timeline rows, the live toggle/indicator bits, and the register readout — so
/// it is rebuilt in place whenever the timeline or the replayed instant changes;
/// nothing is re-evaluated for the picture. The lane set mirrors the bus and
/// readout views: named network inputs first, then named output taps, then the
/// register outputs no named tap covers. Unnamed <c>&lt;gate&gt;.&lt;pin&gt;</c>
/// pins get no lane in this slice.
/// </summary>
public partial class LogicPanelViewModel
{
    /// <summary>
    /// The waveform strip's display model, rebuilt with every timeline change; null
    /// while the timeline is empty. The replay cursor rides on it: selecting a
    /// timeline row rebuilds the model with the cursor at that event's time.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWaveform))]
    private LogicWaveformModel? _waveform;

    /// <summary>True when the strip has at least one named signal lane to draw.</summary>
    public bool HasWaveform => Waveform is { Lanes.Count: > 0 };

    /// <summary>
    /// Rebuilds the waveform model from the current timeline, live bits, and replay
    /// selection. Called at the end of every timeline mutation (toggle, clock step,
    /// reset, clear) and on every selection change (the cursor).
    /// </summary>
    private void RefreshWaveform()
    {
        if (_network == null || TimelineEvents.Count == 0)
        {
            Waveform = null;
            return;
        }
        Waveform = LogicWaveformMapper.Build(
            CollectWaveformLanes(), TimelineEvents, SelectedTimelineEvent?.Event.TimePicoseconds);
    }

    /// <summary>
    /// The lane set in display order: named inputs (a signal-named pin merged into a
    /// network input reads under its signal name, issue #1025), then named outputs
    /// (a tap reading its signal name instead of the raw <c>&lt;gate&gt;.&lt;pin&gt;</c>
    /// id, issue #1046), then register outputs no named tap already covers — the SR
    /// latch's Q and Q̄ are named taps on the register pins, so they appear exactly
    /// once, in the outputs block.
    /// </summary>
    private List<LogicWaveformLaneSource> CollectWaveformLanes()
    {
        var lanes = new List<LogicWaveformLaneSource>();
        var namedInputSignals = PersistedInputSignalNamesByGate()
            .SelectMany(names => names.Value.Values)
            .ToHashSet();
        foreach (var input in Inputs)
        {
            if (namedInputSignals.Contains(input.PinName))
                lanes.Add(new LogicWaveformLaneSource(input.PinName, null, input.IsOn));
        }
        var coveredPins = new HashSet<LogicPinRef>();
        foreach (var output in Outputs)
        {
            if (output.PinName == output.RawPinName)
                continue;
            var pin = _network!.OutputTaps[output.PinName];
            lanes.Add(new LogicWaveformLaneSource(output.PinName, pin, output.IsOne));
            coveredPins.Add(pin);
        }
        foreach (var pin in _network!.RegisterState.Keys
                     .OrderBy(pin => pin.GateId, StringComparer.Ordinal)
                     .ThenBy(pin => pin.PinName, StringComparer.Ordinal))
        {
            if (coveredPins.Contains(pin))
                continue;
            lanes.Add(new LogicWaveformLaneSource(
                $"{pin.GateId}.{pin.PinName}", pin, _network.RegisterState[pin]));
        }
        return lanes;
    }
}
