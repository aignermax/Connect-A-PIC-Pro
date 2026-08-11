using System.Numerics;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Core;
using CAP_Core.ExternalPorts;
using CAP_Core.Grid;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.ViewModels.Analysis.WavelengthSpectrum;

/// <summary>
/// Everything the spectrum sweep needs from the canvas: the simulation grid,
/// the configured light-source ports, human-readable pin labels, the output
/// coupler pins to plot and the design wavelength to mark.
/// </summary>
/// <param name="GridManager">Simulation grid built from the canvas.</param>
/// <param name="Ports">Port manager holding the configured light sources.</param>
/// <param name="PinNames">Flow-id → human-readable pin label.</param>
/// <param name="OutputCouplerPinIds">Light pins of couplers whose laser is off (the design outputs).</param>
/// <param name="DesignWavelengthNm">Wavelength of the first enabled laser (fallback: 1550 nm).</param>
internal sealed record SpectrumCircuit(
    GridManager GridManager,
    PhysicalExternalPortManager Ports,
    IReadOnlyDictionary<Guid, string> PinNames,
    HashSet<Guid> OutputCouplerPinIds,
    int DesignWavelengthNm);

/// <summary>
/// Builds the simulation circuit for the spectrum tab from the current canvas.
/// Follows the same input/output convention as the Transient and Eye tabs:
/// couplers with the laser ON inject light, couplers with the laser OFF are
/// the listen-only outputs whose transmission is plotted.
/// </summary>
internal static class SpectrumSweepCircuitFactory
{
    /// <summary>
    /// Creates the circuit, or null when the canvas is empty.
    /// </summary>
    /// <param name="canvas">Canvas providing components and connections.</param>
    public static SpectrumCircuit? Create(DesignCanvasViewModel canvas)
    {
        if (canvas.Components.Count == 0) return null;

        var tileManager = new ComponentListTileManager();
        foreach (var compVm in canvas.Components)
            tileManager.AddComponent(compVm.Component);

        var portManager = new PhysicalExternalPortManager();
        int designWavelengthNm = ConfigureLightSources(canvas, portManager);

        var gridManager = GridManager.CreateForSimulation(
            tileManager, canvas.ConnectionManager, portManager);

        return new SpectrumCircuit(
            gridManager,
            portManager,
            BuildPinNameMap(canvas),
            TransientCircuitFactory.CollectOutputCouplerPinIds(canvas),
            designWavelengthNm);
    }

    /// <summary>
    /// Registers a light source on every light pin of each laser-on coupler and
    /// returns the design wavelength (first enabled laser, fallback 1550 nm).
    /// </summary>
    private static int ConfigureLightSources(
        DesignCanvasViewModel canvas, PhysicalExternalPortManager portManager)
    {
        int? designWavelengthNm = null;
        foreach (var compVm in canvas.Components)
        {
            if (!compVm.IsLightSource) continue;
            if (compVm.IsLaserOff) continue;

            var laserConfig = compVm.LaserConfig;
            double power = laserConfig?.InputPower ?? 1.0;
            designWavelengthNm ??= laserConfig?.WavelengthNm;

            foreach (var pin in compVm.Component.PhysicalPins)
            {
                if (pin.LogicalPin?.MatterType != MatterType.Light) continue;
                var input = new ExternalInput(
                    $"spectrum_{compVm.Component.Identifier}_{pin.Name}",
                    LaserType.Red, 0, new Complex(power, 0));
                portManager.AddLightSource(input, pin.LogicalPin.IDInFlow);
            }
        }
        return designWavelengthNm ?? StandardWaveLengths.RedNM;
    }

    /// <summary>
    /// Maps every light-pin flow id on the canvas (groups included) to a
    /// human-readable "Component.pin" label for legend and tracker text.
    /// </summary>
    private static Dictionary<Guid, string> BuildPinNameMap(DesignCanvasViewModel canvas)
    {
        var map = new Dictionary<Guid, string>();
        foreach (var component in SimulationService.GetAllComponentsRecursively(canvas.Components))
        {
            var displayName = !string.IsNullOrEmpty(component.HumanReadableName)
                ? component.HumanReadableName!
                : component.Name;

            foreach (var pin in component.PhysicalPins)
            {
                if (pin.LogicalPin == null) continue;
                var pinLabel = $"{displayName}.{pin.Name}";
                map[pin.LogicalPin.IDInFlow] = pinLabel;
                map[pin.LogicalPin.IDOutFlow] = pinLabel;
            }
        }
        return map;
    }
}
