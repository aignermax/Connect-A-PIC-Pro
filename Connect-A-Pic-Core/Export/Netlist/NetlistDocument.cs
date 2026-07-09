namespace CAP_Core.Export.Netlist;

/// <summary>
/// One component instance in a derived netlist. <paramref name="ComponentRef"/> is the
/// PDK factory/cell name the instance references (gdsfactory cell name when the component
/// is gdsfactory-backed, else its Nazca function name) — never a fabricated model value.
/// </summary>
/// <param name="Name">Unique, sanitised instance name (netlist key).</param>
/// <param name="ComponentRef">PDK component/factory name this instance references.</param>
/// <param name="Settings">Parsed instance parameters (may be empty, never null).</param>
public sealed record NetlistInstance(
    string Name,
    string ComponentRef,
    IReadOnlyDictionary<string, string> Settings);

/// <summary>
/// Physical placement of an instance in gdsfactory (Y-up) coordinates, micrometres.
/// </summary>
/// <param name="InstanceName">Instance the placement belongs to.</param>
/// <param name="X">X position in micrometres.</param>
/// <param name="Y">Y position in micrometres (Y-up, gdsfactory convention).</param>
/// <param name="RotationDegrees">Counter-clockwise rotation in degrees.</param>
public sealed record NetlistPlacement(
    string InstanceName,
    double X,
    double Y,
    double RotationDegrees);

/// <summary>
/// One topology edge: a routed connection between two instance ports.
/// </summary>
/// <param name="InstanceA">First endpoint's instance name.</param>
/// <param name="PortA">First endpoint's port (pin) name.</param>
/// <param name="InstanceB">Second endpoint's instance name.</param>
/// <param name="PortB">Second endpoint's port (pin) name.</param>
/// <param name="IsElectrical">True when both pins are electrical (metal trace, issue #682).</param>
public sealed record NetlistConnection(
    string InstanceA,
    string PortA,
    string InstanceB,
    string PortB,
    bool IsElectrical);

/// <summary>
/// A top-level port of the circuit: an instance pin not consumed by any connection,
/// exposed so external tools can address the circuit's inputs/outputs.
/// </summary>
/// <param name="Name">Unique top-level port name.</param>
/// <param name="InstanceName">Instance the port belongs to.</param>
/// <param name="PinName">Pin name on that instance.</param>
public sealed record NetlistPort(string Name, string InstanceName, string PinName);

/// <summary>
/// The circuit topology of a design: instances, placements, connections and top-level
/// ports. Deliberately carries no physics — S-matrices stay in the PDK; a circuit
/// simulator combines this topology with per-component models (issue #687).
/// </summary>
public sealed class NetlistDocument
{
    /// <summary>Circuit name (netlist <c>name:</c> key).</summary>
    public string Name { get; }

    /// <summary>All component instances, in canvas order.</summary>
    public IReadOnlyList<NetlistInstance> Instances { get; }

    /// <summary>Placements, one per instance, same order as <see cref="Instances"/>.</summary>
    public IReadOnlyList<NetlistPlacement> Placements { get; }

    /// <summary>Deduplicated topology edges.</summary>
    public IReadOnlyList<NetlistConnection> Connections { get; }

    /// <summary>Unconnected instance pins exposed as top-level circuit ports.</summary>
    public IReadOnlyList<NetlistPort> Ports { get; }

    /// <summary>Initializes an immutable netlist document.</summary>
    public NetlistDocument(
        string name,
        IReadOnlyList<NetlistInstance> instances,
        IReadOnlyList<NetlistPlacement> placements,
        IReadOnlyList<NetlistConnection> connections,
        IReadOnlyList<NetlistPort> ports)
    {
        Name = name;
        Instances = instances;
        Placements = placements;
        Connections = connections;
        Ports = ports;
    }
}
