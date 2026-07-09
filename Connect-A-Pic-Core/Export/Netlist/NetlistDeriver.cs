using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Components.PinKinds;

namespace CAP_Core.Export.Netlist;

/// <summary>
/// Derives a <see cref="NetlistDocument"/> (circuit topology) from the design canvas:
/// placed components become instances, routed optical/electrical connections become
/// topology edges, and unconnected pins are exposed as top-level ports (issue #687).
/// Groups are flattened to leaf components and group-internal frozen paths are
/// included, mirroring the SAX exporter's flattening contract.
/// </summary>
public class NetlistDeriver
{
    /// <summary>Fallback circuit name when the caller provides none.</summary>
    public const string DefaultDesignName = "lunima_design";

    /// <summary>Derives the netlist for the given design.</summary>
    /// <param name="components">Top-level canvas components (groups are flattened).</param>
    /// <param name="connections">Top-level routed connections between pins.</param>
    /// <param name="designName">Circuit name; blank falls back to <see cref="DefaultDesignName"/>.</param>
    public NetlistDocument Derive(
        IEnumerable<Component> components,
        IEnumerable<WaveguideConnection> connections,
        string? designName = null)
    {
        var topLevel = components.ToList();
        var leafComponents = Flatten(topLevel).Where(c => !c.IsAnalysisTool).ToList();

        var allConnections = connections.ToList();
        allConnections.AddRange(CollectGroupInternalConnections(topLevel));
        var edges = ResolveAndDeduplicate(allConnections);

        var names = NetlistInstanceNamer.BuildNameMap(leafComponents);
        var instances = leafComponents.Select(c => BuildInstance(c, names[c])).ToList();
        var placements = leafComponents.Select(c => BuildPlacement(c, names[c])).ToList();
        var netlistConnections = edges
            .Where(e => names.ContainsKey(e.Start.ParentComponent)
                     && names.ContainsKey(e.End.ParentComponent))
            .Select(e => new NetlistConnection(
                names[e.Start.ParentComponent], e.Start.Name,
                names[e.End.ParentComponent], e.End.Name,
                IsElectrical(e)))
            .ToList();
        var ports = CollectUnconnectedPorts(leafComponents, names, edges);

        var name = string.IsNullOrWhiteSpace(designName) ? DefaultDesignName : designName!;
        return new NetlistDocument(name, instances, placements, netlistConnections, ports);
    }

    private static NetlistInstance BuildInstance(Component comp, string instanceName) =>
        new(instanceName,
            ComponentReferenceOf(comp),
            NetlistSettingsParser.Parse(comp.NazcaFunctionParameters));

    /// <summary>
    /// The PDK component/factory name the instance references: the gdsfactory cell name
    /// (last segment of a module-qualified <see cref="Component.GdsFactoryFunction"/>,
    /// matching how the GDS export resolves cells from the active PDK), else the Nazca
    /// function name, else the instance name itself as a last resort.
    /// </summary>
    private static string ComponentReferenceOf(Component comp)
    {
        var gdsFactory = comp.GdsFactoryFunction;
        if (!string.IsNullOrEmpty(gdsFactory))
            return gdsFactory[(gdsFactory.LastIndexOf('.') + 1)..];
        if (!string.IsNullOrEmpty(comp.NazcaFunctionName))
            return comp.NazcaFunctionName;
        return comp.Name ?? "unknown";
    }

    /// <summary>
    /// Placement in gdsfactory (Y-up) coordinates via the same mapper the GDS/gdsfactory
    /// exports use, so netlist placements and exported layouts agree.
    /// </summary>
    private static NetlistPlacement BuildPlacement(Component comp, string instanceName)
    {
        var placement = NazcaCoordinateMapper.GetCellPlacement(comp, rawOverrideAnchor: null);
        return new NetlistPlacement(instanceName, placement.X, placement.Y, placement.RotationDegrees);
    }

    private static bool IsElectrical(ResolvedConnection edge) =>
        PinKindHelper.IsElectrical(edge.Start) && PinKindHelper.IsElectrical(edge.End);

    /// <summary>
    /// Resolves connection endpoints through ComponentGroup layers to leaf pins and
    /// collapses duplicate edges (a connection can appear both on the canvas and in a
    /// group's InternalPaths). Connections touching analysis tools are dropped.
    /// </summary>
    private static List<ResolvedConnection> ResolveAndDeduplicate(
        IEnumerable<WaveguideConnection> connections)
    {
        var seen = new HashSet<(PhysicalPin, PhysicalPin)>();
        var result = new List<ResolvedConnection>();
        foreach (var conn in connections)
        {
            if (conn.StartPin == null || conn.EndPin == null) continue;
            var start = SaxScriptWriter.ResolveToLeafPin(conn.StartPin);
            var end = SaxScriptWriter.ResolveToLeafPin(conn.EndPin);
            if (start.ParentComponent?.IsAnalysisTool == true) continue;
            if (end.ParentComponent?.IsAnalysisTool == true) continue;
            if (seen.Contains((start, end)) || seen.Contains((end, start))) continue;
            seen.Add((start, end));
            result.Add(new ResolvedConnection(start, end));
        }
        return result;
    }

    /// <summary>
    /// Pins not consumed by any edge become top-level circuit ports, named
    /// <c>{instance}_{pin}</c> (deduplicated) so external tools can address them.
    /// </summary>
    private static List<NetlistPort> CollectUnconnectedPorts(
        IReadOnlyList<Component> leafComponents,
        IReadOnlyDictionary<Component, string> names,
        IReadOnlyList<ResolvedConnection> edges)
    {
        var connectedPins = new HashSet<PhysicalPin>();
        foreach (var edge in edges)
        {
            connectedPins.Add(edge.Start);
            connectedPins.Add(edge.End);
        }

        var usedPortNames = new HashSet<string>(StringComparer.Ordinal);
        var ports = new List<NetlistPort>();
        foreach (var comp in leafComponents)
        {
            foreach (var pin in comp.PhysicalPins)
            {
                if (connectedPins.Contains(pin)) continue;
                var baseName = $"{names[comp]}_{NetlistInstanceNamer.Sanitize(pin.Name)}";
                var portName = baseName;
                for (var suffix = 2; !usedPortNames.Add(portName); suffix++)
                    portName = $"{baseName}_{suffix}";
                ports.Add(new NetlistPort(portName, names[comp], pin.Name));
            }
        }
        return ports;
    }

    private static IEnumerable<Component> Flatten(IEnumerable<Component> components)
    {
        foreach (var comp in components)
        {
            if (comp is ComponentGroup group)
            {
                foreach (var child in group.GetAllComponentsRecursive())
                    yield return child;
            }
            else
            {
                yield return comp;
            }
        }
    }

    /// <summary>
    /// Group-internal frozen paths are connections too — without them, wiring inside a
    /// group would silently vanish from the netlist (same contract as the SAX exporter).
    /// </summary>
    private static IEnumerable<WaveguideConnection> CollectGroupInternalConnections(
        IEnumerable<Component> components)
    {
        foreach (var comp in components)
        {
            if (comp is not ComponentGroup group) continue;

            foreach (var frozen in group.InternalPaths)
            {
                if (frozen.StartPin == null || frozen.EndPin == null) continue;
                yield return new WaveguideConnection
                {
                    StartPin = frozen.StartPin,
                    EndPin = frozen.EndPin,
                };
            }

            foreach (var nested in CollectGroupInternalConnections(group.ChildComponents))
                yield return nested;
        }
    }
}
