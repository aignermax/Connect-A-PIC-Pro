using System.Globalization;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;

namespace CAP_Core.Export;

/// <summary>
/// Exports Lunima photonic designs to PhotonTorch Python scripts that use the
/// real context-manager Network API:
/// <c>with pt.Network() as nw: nw.x = pt.X(...); nw.link('x:i', 'j:y')</c>.
///
/// Ports are addressed by zero-based index (position in <c>PhysicalPins</c>).
/// Every unconnected port is auto-terminated — the first becomes <c>pt.Source()</c>,
/// the rest <c>pt.Detector()</c>.
///
/// Errors are loud: unmapped pins, unknown component types, or designs with
/// no unconnected port throw <see cref="InvalidOperationException"/> rather
/// than silently producing a broken script.
/// </summary>
public class PhotonTorchExporter
{
    /// <summary>Options controlling the generated PhotonTorch simulation script.</summary>
    public class ExportOptions
    {
        /// <summary>Wavelength in nanometers for simulation.</summary>
        public double WavelengthNm { get; init; } = 1550.0;

        /// <summary>Simulation mode: steady-state or time-domain.</summary>
        public SimulationMode Mode { get; init; } = SimulationMode.SteadyState;

        /// <summary>Bit rate in gigabits per second for time-domain simulation.</summary>
        public double BitRateGbps { get; init; } = 1.0;

        /// <summary>Number of time steps for time-domain simulation.</summary>
        public int TimeDomainSteps { get; init; } = 1000;
    }

    /// <summary>Simulation mode for the generated PhotonTorch script.</summary>
    public enum SimulationMode
    {
        /// <summary>Continuous-wave, frequency-domain steady-state.</summary>
        SteadyState,

        /// <summary>Time-domain pulsed simulation.</summary>
        TimeDomain
    }

    /// <summary>
    /// Exports the design to a PhotonTorch Python script.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a component type has no known PhotonTorch mapping, when a
    /// connection references unmapped pins, when a component has null
    /// <see cref="Component.PhysicalPins"/>, or when no unconnected port is
    /// available to serve as <c>pt.Source()</c>.
    /// </exception>
    public string Export(
        IReadOnlyList<Component> components,
        IReadOnlyList<WaveguideConnection> connections,
        ExportOptions? options = null)
    {
        options ??= new ExportOptions();

        // Virtual analysis tools (e.g. ONA Analyzer) have no physical counterpart
        // and no PhotonTorch mapping — strip them and any connections that touch
        // them before processing so callers don't have to think about it.
        components = components.Where(c => !c.IsAnalysisTool).ToList();
        connections = connections
            .Where(c => c.StartPin?.ParentComponent?.IsAnalysisTool != true
                     && c.EndPin?.ParentComponent?.IsAnalysisTool != true)
            .ToList();

        ValidateComponentPins(components);

        var nameMap = ComponentNameMap.Build(components);
        var pinIndexMap = BuildPinIndexMap(components);
        var connectedPins = CollectConnectedPins(connections);
        var terminations = BuildTerminations(components, connectedPins, nameMap, pinIndexMap);

        if (terminations.Count == 0 || !terminations[0].IsSource)
        {
            throw new InvalidOperationException(
                "PhotonTorch export requires at least one unconnected port to act as Source. " +
                "The given design has none — every port is wired to another component.");
        }

        return new PhotonTorchScriptWriter(CultureInfo.InvariantCulture)
            .Write(components, connections, options, nameMap, pinIndexMap, terminations);
    }

    private static void ValidateComponentPins(IReadOnlyList<Component> components)
    {
        foreach (var comp in components)
        {
            if (comp.PhysicalPins == null)
            {
                throw new InvalidOperationException(
                    $"Component '{comp.Identifier}' has null PhysicalPins. PhotonTorch export " +
                    "needs each component's port list to generate numeric indices.");
            }
        }
    }

    private static Dictionary<Guid, int> BuildPinIndexMap(IReadOnlyList<Component> components)
    {
        var map = new Dictionary<Guid, int>();
        foreach (var comp in components)
        {
            for (int i = 0; i < comp.PhysicalPins.Count; i++)
                map[comp.PhysicalPins[i].PinId] = i;
        }
        return map;
    }

    private static HashSet<Guid> CollectConnectedPins(IReadOnlyList<WaveguideConnection> connections)
    {
        var set = new HashSet<Guid>();
        foreach (var conn in connections)
        {
            set.Add(conn.StartPin.PinId);
            set.Add(conn.EndPin.PinId);
        }
        return set;
    }

    /// <summary>
    /// Allocates a Source/Detector variable name for every unconnected pin.
    /// The first unconnected pin becomes <c>pt.Source()</c>, the rest become Detectors.
    /// </summary>
    private static List<Termination> BuildTerminations(
        IReadOnlyList<Component> components,
        HashSet<Guid> connectedPins,
        ComponentNameMap nameMap,
        Dictionary<Guid, int> pinIndexMap)
    {
        var result = new List<Termination>();
        int detectorIndex = 0;
        bool sourceAssigned = false;

        foreach (var comp in components)
        {
            foreach (var pin in comp.PhysicalPins)
            {
                if (connectedPins.Contains(pin.PinId)) continue;
                // Only optical pins terminate as photonic Source/Detector — an electrical pin
                // is not an optical port, and auto-terminating it would inject/measure light on
                // a metal contact and corrupt the optical netlist (#519).
                if (pin.MatterType != MatterType.Light) continue;

                var compVar = nameMap[comp.Identifier];
                var portIndex = pinIndexMap[pin.PinId];

                if (!sourceAssigned)
                {
                    result.Add(new Termination("src", IsSource: true, compVar, portIndex));
                    sourceAssigned = true;
                }
                else
                {
                    result.Add(new Termination($"det_{detectorIndex++}", IsSource: false, compVar, portIndex));
                }
            }
        }

        return result;
    }
}
