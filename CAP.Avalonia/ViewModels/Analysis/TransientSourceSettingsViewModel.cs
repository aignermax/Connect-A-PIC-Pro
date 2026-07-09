using CAP_Core.LightCalculation.TimeDomainSimulation;
using CAP_Core.LightCalculation.TimeDomainSimulation.Sampling;
using CAP_Core.LightCalculation.TimeDomainSimulation.Sources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Analysis;

/// <summary>Input waveform families selectable for the transient run (issue #600).</summary>
public enum TransientSourceType
{
    /// <summary>Single Gaussian pulse (pre-#600 behaviour, default).</summary>
    GaussianPulse,

    /// <summary>Continuous wave: constant envelope over the whole run.</summary>
    ContinuousWave,

    /// <summary>PRBS-NRZ data stream on a signal-driven time grid.</summary>
    PrbsNrz,
}

/// <summary>
/// Signal-source selection and parameters for the transient panel
/// (issue #600, D5): which <see cref="ISignalSource"/> drives the input pins
/// and — for PRBS — the signal-driven time grid via <see cref="SamplingPolicy"/>.
/// </summary>
public partial class TransientSourceSettingsViewModel : ObservableObject
{
    /// <summary>All source types, for the panel's ComboBox.</summary>
    public static IReadOnlyList<TransientSourceType> SourceTypes { get; } =
        new[]
        {
            TransientSourceType.GaussianPulse,
            TransientSourceType.ContinuousWave,
            TransientSourceType.PrbsNrz,
        };

    /// <summary>Supported PRBS orders, for the panel's ComboBox.</summary>
    public static IReadOnlyList<int> PrbsOrders { get; } =
        PrbsBitGenerator.SupportedOrders.ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPrbs))]
    [NotifyPropertyChangedFor(nameof(IsGaussianPulse))]
    private TransientSourceType _sourceType = TransientSourceType.GaussianPulse;

    [ObservableProperty]
    private double _bitrateGbps = 25;

    [ObservableProperty]
    private int _prbsOrder = 7;

    [ObservableProperty]
    private int _samplesPerSymbol = 32;

    [ObservableProperty]
    private int _symbolCount = 32;

    [ObservableProperty]
    private double _extinctionRatioDb = 10;

    [ObservableProperty]
    private int _seed = 1;

    /// <summary>True while the PRBS parameter fields should be visible.</summary>
    public bool IsPrbs => SourceType == TransientSourceType.PrbsNrz;

    /// <summary>True while the Gaussian pulse parameter fields should be visible.</summary>
    public bool IsGaussianPulse => SourceType == TransientSourceType.GaussianPulse;

    /// <summary>
    /// Creates the time grid for the selected source. PRBS runs use the
    /// signal-driven grid (bitrate × samples-per-symbol, guard = IR length,
    /// design #600 D1/D4); the other sources keep the pre-#600
    /// wavelength-sweep grid for back-compat.
    /// </summary>
    /// <param name="centerWavelengthNm">Sweep centre wavelength in nm.</param>
    /// <param name="spanNm">Sweep span in nm.</param>
    /// <param name="freqPoints">Frequency points (= impulse-response length).</param>
    public TimeSignalDefinition CreateGrid(double centerWavelengthNm, double spanNm, int freqPoints)
        => SourceType == TransientSourceType.PrbsNrz
            ? SamplingPolicy.CreateGrid(BitrateGbps * 1e9, SamplesPerSymbol, SymbolCount, freqPoints)
            : TimeSignalDefinition.FromWavelengthSweep(centerWavelengthNm, spanNm, freqPoints);

    /// <summary>
    /// Creates the signal source for one input pin.
    /// </summary>
    /// <param name="amplitude">Pin envelope amplitude (√W, from the pin's input power).</param>
    /// <param name="pulseCenterSeconds">Gaussian pulse centre (Gaussian only).</param>
    /// <param name="pulseSigmaSeconds">Gaussian 1-σ width (Gaussian only).</param>
    public ISignalSource CreateSource(
        double amplitude, double pulseCenterSeconds, double pulseSigmaSeconds)
        => SourceType switch
        {
            TransientSourceType.ContinuousWave => new CwSource(amplitude),
            TransientSourceType.PrbsNrz => new PrbsSource(
                BitrateGbps * 1e9, PrbsOrder, amplitude, ExtinctionRatioDb, Seed),
            _ => new PulseSource(pulseCenterSeconds, pulseSigmaSeconds, amplitude),
        };
}
