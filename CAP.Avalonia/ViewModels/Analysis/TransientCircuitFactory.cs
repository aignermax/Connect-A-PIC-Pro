using System.Numerics;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.ExternalPorts;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using CAP_Core.LightCalculation.TimeDomainSimulation;
using CAP.Avalonia.ViewModels.Canvas;

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
    /// Every non-directional coupler on the canvas is treated as a laser input.
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

    /// <summary>Registers a light source on every light pin of each input coupler.</summary>
    private static void ConfigureLightSources(
        DesignCanvasViewModel canvas, PhysicalExternalPortManager portManager)
    {
        foreach (var compVm in canvas.Components)
        {
            if (compVm.TemplateName == null) continue;
            if (!compVm.TemplateName.Contains("Coupler", StringComparison.OrdinalIgnoreCase)) continue;
            if (compVm.TemplateName.Contains("Directional", StringComparison.OrdinalIgnoreCase)) continue;

            var laserConfig = compVm.LaserConfig;
            double power = laserConfig?.InputPower ?? 1.0;
            var laserType = laserConfig?.WavelengthNm == StandardWaveLengths.GreenNM
                ? LaserType.Green
                : laserConfig?.WavelengthNm == StandardWaveLengths.BlueNM
                    ? LaserType.Blue
                    : LaserType.Red;

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
