using System.Numerics;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Core;
using CAP_Core.ExternalPorts;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.ViewModels.Analysis.CircuitOptimization;

/// <summary>
/// Builds a reusable <see cref="ILightCalculator"/> over the live canvas design,
/// with light sources configured from the couplers' laser settings. The optimizer
/// re-runs this calculator for every candidate; slider changes are picked up
/// because the system matrix is rebuilt per evaluation.
/// </summary>
public static class CanvasSimulationBuilder
{
    /// <summary>
    /// Creates a calculator for the current canvas, or null when the design has
    /// no components, no connections, or no active light source.
    /// </summary>
    public static ILightCalculator? TryBuild(DesignCanvasViewModel canvas)
    {
        if (canvas.Components.Count == 0 || canvas.Connections.Count == 0)
            return null;

        var tileManager = new ComponentListTileManager();
        foreach (var componentVm in canvas.Components)
            tileManager.AddComponent(componentVm.Component);

        var portManager = new PhysicalExternalPortManager();
        if (!ConfigureLightSources(canvas, portManager))
            return null;

        var gridManager = GridManager.CreateForSimulation(
            tileManager, canvas.ConnectionManager, portManager);

        return new GridLightCalculator(new SystemMatrixBuilder(gridManager), gridManager);
    }

    private static bool ConfigureLightSources(
        DesignCanvasViewModel canvas,
        PhysicalExternalPortManager portManager)
    {
        bool anySource = false;
        foreach (var componentVm in canvas.Components)
        {
            if (!componentVm.IsLightSource) continue;
            if (componentVm.IsLaserOff) continue;

            anySource |= AddSourcePins(componentVm, portManager);
        }
        return anySource;
    }

    private static bool AddSourcePins(
        ComponentViewModel componentVm,
        PhysicalExternalPortManager portManager)
    {
        var laserConfig = componentVm.LaserConfig;
        double power = laserConfig?.InputPower ?? 1.0;
        var laserType = SimulationService.GetLaserTypeForWavelength(
            laserConfig?.WavelengthNm ?? StandardWaveLengths.RedNM);

        bool added = false;
        foreach (var pin in componentVm.Component.PhysicalPins)
        {
            if (pin.LogicalPin?.MatterType != MatterType.Light) continue;
            var input = new ExternalInput(
                $"src_{componentVm.Component.Identifier}_{pin.Name}",
                laserType, 0, new Complex(power, 0));
            portManager.AddLightSource(input, pin.LogicalPin.IDInFlow);
            added = true;
        }
        return added;
    }
}
