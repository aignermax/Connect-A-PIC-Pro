namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>One endpoint of a logic net: one pin of one gate instance in the network.</summary>
/// <param name="GateId">The network-local id of the gate instance.</param>
/// <param name="PinName">The pin name on that gate.</param>
public sealed record LogicPinRef(string GateId, string PinName);

/// <summary>
/// One inter-gate wire of the logic network: a gate output pin driving a gate input pin.
/// </summary>
/// <param name="Source">The driving gate output pin.</param>
/// <param name="Load">The driven gate input pin.</param>
public sealed record LogicWireEdge(LogicPinRef Source, LogicPinRef Load);

/// <summary>The driver of a gate input pin: a network-level input or another gate's output.</summary>
public abstract record LogicNetDriver
{
    private LogicNetDriver()
    {
    }

    /// <summary>A network-level input pin drives the gate input.</summary>
    /// <param name="PinName">The network input pin name.</param>
    public sealed record NetworkInput(string PinName) : LogicNetDriver;

    /// <summary>An output pin of another gate instance drives the gate input.</summary>
    /// <param name="Pin">The driving gate output pin.</param>
    public sealed record GateOutput(LogicPinRef Pin) : LogicNetDriver;
}
