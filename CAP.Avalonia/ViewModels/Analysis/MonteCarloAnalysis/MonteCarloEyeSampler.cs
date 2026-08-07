using CAP_Core.Analysis.EyeDiagram;
using CAP_Core.LightCalculation;
using CAP_Core.LightCalculation.TimeDomainSimulation;
using CAP.Avalonia.ViewModels.Analysis.AnalysisOutput;
using CAP.Avalonia.ViewModels.Analysis.EyeDiagram;
using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.ViewModels.Analysis.MonteCarloAnalysis;

/// <summary>
/// Per-run metric source for the eye-openness Monte-Carlo mode: each call runs
/// one PRBS transient simulation under the CURRENTLY active fabrication-variance
/// sample and returns the eye height (vertical eye opening) as a one-element
/// curve. The simulator is rebuilt per run so the current variance sample flows
/// into fresh system matrices. Uses the Eye/BER tab's default receiver
/// settings so both views describe the same eye.
/// </summary>
internal sealed class MonteCarloEyeSampler
{
    private const double DefaultBitRateHz = 25e9;
    private const double DefaultThresholdRelative = 0.5;
    private const PrbsOrder DefaultPrbsOrder = PrbsOrder.Prbs7;

    /// <summary>Receiver electrical bandwidth as a fraction of the bit rate (typical NRZ receiver).</summary>
    private const double ReceiverBandwidthFactor = 0.75;

    private readonly DesignCanvasViewModel _canvas;
    private readonly HashSet<Guid>? _designatedPinIds;
    private readonly bool _hasMultipleCandidates;
    private readonly Func<ISystemMatrixBuilder, ISystemMatrixBuilder>? _decorateBuilder;

    /// <summary>Creates a sampler bound to the canvas and the resolved analysis output (#754).</summary>
    /// <param name="canvas">Canvas providing components and connections.</param>
    /// <param name="resolution">Resolved analysis-output designation.</param>
    /// <param name="decorateBuilder">
    /// Optional system-matrix decorator; the Monte-Carlo run passes the fabrication
    /// perturbation here so each transient simulation sees the current variance sample.
    /// </param>
    public MonteCarloEyeSampler(
        DesignCanvasViewModel canvas, AnalysisOutputResolution resolution,
        Func<ISystemMatrixBuilder, ISystemMatrixBuilder>? decorateBuilder = null)
    {
        _canvas = canvas;
        _decorateBuilder = decorateBuilder;
        _designatedPinIds = resolution.State == AnalysisOutputState.DesignatedValid
            ? AnalysisOutputResolver.CollectLightPinIds(resolution.Output!)
            : null;
        _hasMultipleCandidates = resolution.State == AnalysisOutputState.MultipleCandidates;
    }

    /// <summary>Runs one PRBS transient simulation off the UI thread and returns [eyeHeight].</summary>
    public Task<double[]> SampleAsync(CancellationToken cancellationToken)
        => Task.Run(() => new[] { RunSingleEyeAnalysis() }, cancellationToken);

    private double RunSingleEyeAnalysis()
    {
        var (simulator, ports) = TransientCircuitFactory.Create(_canvas, decorateBuilder: _decorateBuilder);
        var outputPinIds = TransientCircuitFactory.CollectOutputCouplerPinIds(_canvas);

        var sweepDef = TimeSignalDefinition.FromWavelengthSweep(
            TimeDomainSimulator.DefaultCenterWavelengthNm,
            TimeDomainSimulator.DefaultSpanNm,
            TimeDomainSimulator.DefaultNPoints);
        var plan = EyeSimulationPlan.Create(
            DefaultBitRateHz, sweepDef.SampleRateHz, PrbsGenerator.PatternLength(DefaultPrbsOrder));
        var bits = PrbsGenerator.GenerateBits(DefaultPrbsOrder, plan.BitCount);
        var timeDef = new TimeSignalDefinition(sweepDef.SampleRateHz, plan.TotalSamples);

        var signals = new Dictionary<Guid, double[]>();
        double injectedPeakPower = 0;
        foreach (var used in ports.GetUsedExternalInputs())
        {
            double amplitude = Math.Sqrt(used.Input.InFlowPower.Magnitude);
            injectedPeakPower = Math.Max(injectedPeakPower, amplitude * amplitude);
            signals[used.AttachedComponentPinId] = PrbsGenerator.ToNrzSamples(bits, plan.SamplesPerBit, amplitude);
        }
        if (signals.Count == 0)
            throw new InvalidOperationException(
                Services.Localization.LocalizationService.Instance.Translate("Analysis.Common.NoLaserOn"));

        var result = simulator.Run(
            signals, timeDef,
            TimeDomainSimulator.DefaultCenterWavelengthNm,
            TimeDomainSimulator.DefaultSpanNm,
            TimeDomainSimulator.DefaultNPoints,
            TransientCircuitFactory.BuildPhaseNoiseSettings(_canvas));

        var selection = EyeTraceSelector.Select(
            result, outputPinIds, _designatedPinIds,
            hasMultipleCandidates: _hasMultipleCandidates,
            injectedPeakPower: injectedPeakPower);
        if (selection.Trace == null)
            throw new InvalidOperationException(selection.Error);

        return EstimateEyeHeight(selection.Trace, timeDef.SampleRateHz, plan);
    }

    private static double EstimateEyeHeight(double[] trace, double sampleRateHz, EyeSimulationPlan plan)
    {
        int timeBins = Math.Min(EyeDiagramBuilder.DefaultTimeBins, plan.SamplesPerBit);
        var histogram = EyeDiagramBuilder.Build(trace, sampleRateHz, plan.BitPeriodSeconds, timeBins);
        double threshold = histogram.MinAmplitude
            + DefaultThresholdRelative * (histogram.MaxAmplitude - histogram.MinAmplitude);
        var noise = new NoiseModel { BandwidthHz = ReceiverBandwidthFactor * DefaultBitRateHz };
        var metrics = BerEstimator.Estimate(
            trace, sampleRateHz, plan.BitPeriodSeconds, threshold, noise, timeBins);
        return metrics.EyeHeight;
    }
}
