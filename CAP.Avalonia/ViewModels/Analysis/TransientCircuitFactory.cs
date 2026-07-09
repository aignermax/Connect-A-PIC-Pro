using System.Numerics;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.ExternalPorts;
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
    public static (TimeDomainSimulator Simulator, PhysicalExternalPortManager Ports) Create(
        DesignCanvasViewModel canvas)
    {
        var tileManager = new ComponentListTileManager();
        foreach (var compVm in canvas.Components)
            tileManager.AddComponent(compVm.Component);

        var portManager = new PhysicalExternalPortManager();
        ConfigureLightSources(canvas, portManager);

        var gridManager = GridManager.CreateForSimulation(
            tileManager, canvas.ConnectionManager, portManager);

        var builder = new SystemMatrixBuilder(gridManager);
        return (new TimeDomainSimulator(builder), portManager);
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
