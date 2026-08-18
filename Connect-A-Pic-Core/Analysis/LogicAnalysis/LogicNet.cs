namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>One endpoint of a logic net: one pin of one gate instance in the network.</summary>
/// <param name="GateId">The network-local id of the gate instance.</param>
/// <param name="PinName">The pin name on that gate.</param>
public sealed record LogicPinRef(string GateId, string PinName);

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

/// <summary>
/// One fan-out finding: a driver — a gate output pin or a network input — feeding more
/// than one gate input. The logic layer hands every load the full level, which optics
/// cannot do (splitting a waveguide divides the power), so the finding is surfaced as a
/// non-blocking warning instead of being silently idealized away.
/// </summary>
/// <param name="PinName">The fanning-out pin, named the way the network names it (<c>gateId.pinName</c> for a gate output, the pin name for a network input).</param>
/// <param name="LoadCount">How many gate inputs the pin drives.</param>
public sealed record LogicFanOutWarning(string PinName, int LoadCount);
