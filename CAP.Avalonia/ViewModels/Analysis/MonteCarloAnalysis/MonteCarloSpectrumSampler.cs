using System.Numerics;
using CAP_Core.Analysis.OnaAnalysis;
using CAP_Core.Components.Core;
using CAP_Core.ExternalPorts;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.ViewModels.Analysis.MonteCarloAnalysis;

/// <summary>
/// Per-run metric source for the spectrum-envelope Monte-Carlo mode: each call
/// sweeps the wavelength range under the CURRENTLY active fabrication-variance
/// sample and returns the insertion-loss curve at the design's output pin.
/// The output pin is chosen once, on the first (nominal) run, and kept stable
/// across all jittered runs so the curves are comparable.
/// </summary>
internal sealed class MonteCarloSpectrumSampler
{
    private readonly WavelengthSweeper _sweeper;
    private readonly GridManager _gridManager;
    private readonly WavelengthSweepConfiguration _config;
    private readonly HashSet<Guid> _preferredOutputPinIds;
    private readonly Dictionary<Guid, string> _pinNames;
    private Guid? _selectedPinId;

    /// <summary>Wavelength values (nm) of every curve index.</summary>
    public int[] Wavelengths { get; }

    /// <summary>Readable name of the evaluated output pin (available after the first run).</summary>
    public string SelectedPinName =>
        _selectedPinId.HasValue && _pinNames.TryGetValue(_selectedPinId.Value, out var name)
            ? name
            : "output";

    private MonteCarloSpectrumSampler(
        WavelengthSweeper sweeper,
        GridManager gridManager,
        WavelengthSweepConfiguration config,
        HashSet<Guid> preferredOutputPinIds,
        Dictionary<Guid, string> pinNames)
    {
        _sweeper = sweeper;
        _gridManager = gridManager;
        _config = config;
        _preferredOutputPinIds = preferredOutputPinIds;
        _pinNames = pinNames;
        Wavelengths = config.GenerateWavelengthValues();
    }

    /// <summary>
    /// Builds the simulation circuit for the given canvas. Returns an error
    /// message instead of a sampler when the design has no light source.
    /// </summary>
    /// <param name="canvas">Canvas providing components and connections.</param>
    /// <param name="config">Sweep range and step count.</param>
    /// <param name="decorateBuilder">
    /// Optional system-matrix decorator; the Monte-Carlo run passes the fabrication
    /// perturbation here so each sweep sees the current variance sample.
    /// </param>
    public static (MonteCarloSpectrumSampler? Sampler, string? Error) Create(
        DesignCanvasViewModel canvas, WavelengthSweepConfiguration config,
        Func<ISystemMatrixBuilder, ISystemMatrixBuilder>? decorateBuilder = null)
    {
        var tileManager = new ComponentListTileManager();
        foreach (var compVm in canvas.Components)
            tileManager.AddComponent(compVm.Component);

        var portManager = new PhysicalExternalPortManager();
        var pinNames = ConfigureLightSourcesAndNames(canvas, portManager);
        if (portManager.GetAllExternalInputs().Count == 0)
            return (null, Services.Localization.LocalizationService.Instance.Translate("Analysis.Common.NoLaserOn"));

        var gridManager = GridManager.CreateForSimulation(
            tileManager, canvas.ConnectionManager, portManager);
        ISystemMatrixBuilder builder = new SystemMatrixBuilder(gridManager);
        if (decorateBuilder != null)
            builder = decorateBuilder(builder);
        var sweeper = new WavelengthSweeper(builder, portManager);
        var outputPins = TransientCircuitFactory.CollectOutputCouplerPinIds(canvas);

        return (new MonteCarloSpectrumSampler(sweeper, gridManager, config, outputPins, pinNames), null);
    }

    /// <summary>Runs one wavelength sweep under the current variance sample and returns the IL curve (dB).</summary>
    public async Task<double[]> SampleAsync(CancellationToken cancellationToken)
    {
        var result = await _sweeper.RunSweepAsync(_config, _gridManager, cancellationToken);
        _selectedPinId ??= ChooseOutputPin(result);
        return result.GetInsertionLossSeriesForPin(_selectedPinId.Value);
    }

    /// <summary>
    /// Picks the pin whose spectrum the envelope tracks: among the output-coupler
    /// pins (laser off) when available, otherwise all monitored pins, the one
    /// with the highest mean transmission on the nominal run.
    /// </summary>
    private Guid ChooseOutputPin(WavelengthSweepResult result)
    {
        var candidates = result.MonitoredPinIds
            .Where(_preferredOutputPinIds.Contains)
            .ToList();
        if (candidates.Count == 0)
            candidates = result.MonitoredPinIds.ToList();

        return candidates
            .OrderByDescending(pin => result.GetInsertionLossSeriesForPin(pin).Average())
            .First();
    }

    /// <summary>
    /// Registers a unit-power input on every light pin of each enabled light
    /// source (same source model as the ONA sweep) and builds a pin-name map
    /// for the plot title.
    /// </summary>
    private static Dictionary<Guid, string> ConfigureLightSourcesAndNames(
        DesignCanvasViewModel canvas, PhysicalExternalPortManager portManager)
    {
        var pinNames = new Dictionary<Guid, string>();
        foreach (var component in SimulationService.GetAllComponentsRecursively(canvas.Components))
        {
            string displayName = string.IsNullOrEmpty(component.HumanReadableName)
                ? component.Name
                : component.HumanReadableName!;
            bool injectsLight = SimulationService.IsLightSource(component) && component.LaserEnabled;

            foreach (var pin in component.PhysicalPins)
            {
                if (pin.LogicalPin?.MatterType != MatterType.Light) continue;
                pinNames[pin.LogicalPin.IDInFlow] = $"{displayName}.{pin.Name}";
                pinNames[pin.LogicalPin.IDOutFlow] = $"{displayName}.{pin.Name}";

                if (!injectsLight) continue;
                var input = new ExternalInput(
                    $"mc_{component.Identifier}_{pin.Name}",
                    LaserType.Red, 0, new Complex(1.0, 0));
                portManager.AddLightSource(input, pin.LogicalPin.IDInFlow);
            }
        }
        return pinNames;
    }
}
