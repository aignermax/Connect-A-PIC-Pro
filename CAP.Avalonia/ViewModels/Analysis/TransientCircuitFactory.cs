using System.Numerics;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.ExternalPorts;
using CAP_Core.ExternalPorts.LaserSpectrum;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using CAP_Core.LightCalculation.TimeDomainSimulation;
using CAP.Avalonia.ViewModels.Canvas;

using CAP.Avalonia.Services;

namespace CAP.Avalonia.ViewModels.Analysis;

/// <summary>
/// Builds a <see cref="TimeDomainSimulator"/> plus the external light-source
/// ports for the current canvas. Shared by the transient panel (#527) and the
/// eye-diagram panel (#535) so both drive the identical circuit setup.
/// </summary>
internal static class TransientCircuitFactory
{
    /// <summary>
    /// Creates the simulator and the port manager holding all configured light sources.
    /// Every non-directional coupler with its laser switched on is treated as a laser
    /// input; couplers with the laser off are listen-only outputs (#690).
    /// </summary>
    /// <param name="canvas">Canvas providing components and connections.</param>
    /// <param name="onPassivityWarning">
    /// Receives at most ONE warning per component per created simulator when a shipped
    /// measured dataset exceeds passivity within the tolerated noise band (the closure
    /// sweeps many wavelengths — without deduplication the console would repeat the
    /// same component hundreds of times).
    /// </param>
    /// <param name="decorateBuilder">
    /// Optional system-matrix decorator; the Monte-Carlo run passes the fabrication
    /// perturbation here so the transient simulation sees the current
    /// variance sample.
    /// </param>
    public static (TimeDomainSimulator Simulator, PhysicalExternalPortManager Ports) Create(
        DesignCanvasViewModel canvas, Action<PassivityWarning>? onPassivityWarning = null,
        Func<ISystemMatrixBuilder, ISystemMatrixBuilder>? decorateBuilder = null)
    {
        var tileManager = new ComponentListTileManager();
        foreach (var compVm in canvas.Components)
            tileManager.AddComponent(compVm.Component);

        var portManager = new PhysicalExternalPortManager();
        ConfigureLightSources(canvas, portManager);

        var gridManager = GridManager.CreateForSimulation(
            tileManager, canvas.ConnectionManager, portManager);

        ISystemMatrixBuilder builder = new SystemMatrixBuilder(gridManager);
        if (decorateBuilder != null)
            builder = decorateBuilder(builder);
        var context = BuildClosureContext(canvas) with
        {
            PassivityWarningSink = DedupePerComponent(onPassivityWarning),
        };
        return (new TimeDomainSimulator(builder, context), portManager);
    }

    /// <summary>Forwards only the FIRST warning per component name to <paramref name="sink"/>.</summary>
    internal static Action<PassivityWarning>? DedupePerComponent(Action<PassivityWarning>? sink)
    {
        if (sink == null)
            return null;
        var warnedComponents = new HashSet<string>();
        return warning =>
        {
            if (warnedComponents.Add(warning.ComponentName))
                sink(warning);
        };
    }

    /// <summary>
    /// Circuit knowledge for the multi-hop closure solve (field round 4, final batch):
    /// pin owner names let a failure name the non-passive component or the feedback
    /// loop; the coupler light pins are the circuit's external ports, so the |H| ≤ 1
    /// energy guard applies there and NOT to pins inside a ring (cavity buildup is
    /// legitimate physics).
    /// </summary>
    /// <param name="canvas">Canvas providing components.</param>
    private static TransitiveClosureContext BuildClosureContext(DesignCanvasViewModel canvas)
    {
        var owners = new Dictionary<Guid, string>();
        var couplerPinIds = new HashSet<Guid>();
        foreach (var compVm in canvas.Components)
        {
            bool isCoupler = compVm.IsLightSource;
            foreach (var pin in compVm.Component.PhysicalPins)
            {
                if (pin.LogicalPin == null) continue;
                owners[pin.LogicalPin.IDInFlow] = compVm.Name;
                owners[pin.LogicalPin.IDOutFlow] = compVm.Name;
                if (isCoupler && pin.LogicalPin.MatterType == MatterType.Light)
                {
                    couplerPinIds.Add(pin.LogicalPin.IDInFlow);
                    couplerPinIds.Add(pin.LogicalPin.IDOutFlow);
                }
            }
        }
        return new TransitiveClosureContext
        {
            PinOwnerNames = owners,
            ExternallyObservablePinIds = couplerPinIds.Count > 0 ? couplerPinIds : null,
        };
    }

    /// <summary>
    /// Collects the light-pin flow ids of every coupler whose laser is switched OFF
    /// (#690). These pins are the design's true outputs: they listen without emitting.
    /// Both flow directions are included so the set matches trace keys regardless of
    /// which flow id the simulator keys a trace by.
    /// </summary>
    /// <param name="canvas">Canvas providing components.</param>
    public static HashSet<Guid> CollectOutputCouplerPinIds(DesignCanvasViewModel canvas)
    {
        var pinIds = new HashSet<Guid>();
        foreach (var compVm in canvas.Components)
        {
            if (!compVm.IsLaserOff) continue;
            foreach (var pin in compVm.Component.PhysicalPins)
            {
                if (pin.LogicalPin?.MatterType != MatterType.Light) continue;
                pinIds.Add(pin.LogicalPin.IDInFlow);
                pinIds.Add(pin.LogicalPin.IDOutFlow);
            }
        }
        return pinIds;
    }

    /// <summary>
    /// Resolves the laser RIN the eye-diagram noise model should use (#819): the
    /// noisiest (largest) RIN among the couplers whose laser is switched on, or the
    /// default DFB value when no source overrides it. Noise contributions add, so
    /// the worst source dominates the amplitude noise floor.
    /// </summary>
    /// <param name="canvas">Canvas providing components.</param>
    public static double ResolveRinDbPerHz(DesignCanvasViewModel canvas)
    {
        double? worst = null;
        foreach (var compVm in canvas.Components)
        {
            if (!LightSourceClassifier.IsLightInjectingCoupler(compVm.TemplateName)) continue;
            if (compVm.IsLaserOff) continue;
            double rin = compVm.LaserConfig?.RinDbPerHz ?? LaserSpectrumModel.DefaultRinDbPerHz;
            worst = worst == null ? rin : Math.Max(worst.Value, rin);
        }
        return worst ?? LaserSpectrumModel.DefaultRinDbPerHz;
    }

    /// <summary>
    /// Registers a light source on every light pin of each input coupler.
    /// Couplers whose laser is switched off (#690) are skipped — they act as outputs.
    /// </summary>
    private static void ConfigureLightSources(
        DesignCanvasViewModel canvas, PhysicalExternalPortManager portManager)
    {
        foreach (var compVm in canvas.Components)
        {
            if (!LightSourceClassifier.IsLightInjectingCoupler(compVm.TemplateName)) continue;
            if (compVm.IsLaserOff) continue;

            var laserConfig = compVm.LaserConfig;
            double power = laserConfig?.InputPower ?? 1.0;
            var laserType = SimulationService.GetLaserTypeForWavelength(
                laserConfig?.WavelengthNm ?? StandardWaveLengths.RedNM);

            foreach (var pin in compVm.Component.PhysicalPins)
            {
                if (pin.LogicalPin?.MatterType != MatterType.Light) continue;
                var input = new ExternalInput(
                    $"src_{compVm.Component.Identifier}_{pin.Name}",
                    laserType, 0, new Complex(power, 0));
                portManager.AddLightSource(input, pin.LogicalPin.IDInFlow);
            }
        }
    }
}
