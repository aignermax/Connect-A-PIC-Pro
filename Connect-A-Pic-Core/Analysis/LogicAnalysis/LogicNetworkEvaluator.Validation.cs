namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// Structural validation half of <see cref="LogicNetworkEvaluator"/>: every check
/// runs once at construction, so an invalid network never reaches evaluation.
/// </summary>
public sealed partial class LogicNetworkEvaluator
{
    /// <summary>
    /// Rejects duplicated input names, and an empty input list unless the network
    /// contains a register — a register-bearing network without inputs is
    /// self-sufficient and stimulated by <see cref="Step"/> alone (e.g. a
    /// free-running counter), while a combinational network without inputs could
    /// never produce anything but constants.
    /// </summary>
    private void ValidateNetworkInputs()
    {
        if (InputPinNames.Count == 0 && _registerGateIds.Count == 0)
            throw new ArgumentException(
                "A logic network needs at least one input pin unless it contains a register.",
                nameof(InputPinNames));
        var duplicate = InputPinNames.GroupBy(name => name).FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate != null)
            throw new ArgumentException($"Network input '{duplicate}' is declared more than once.", nameof(InputPinNames));
    }

    /// <summary>Rejects an empty gate list or null gate instances.</summary>
    private void ValidateGates()
    {
        if (Gates.Count == 0)
            throw new ArgumentException("A logic network needs at least one gate.", nameof(Gates));
        var nullGate = Gates.FirstOrDefault(pair => pair.Value == null).Key;
        if (nullGate != null)
            throw new ArgumentException($"Gate '{nullGate}' is null.", nameof(Gates));
    }

    /// <summary>
    /// Verifies every wiring endpoint: loads must be real input pins of real gates,
    /// drivers must be declared network inputs or real output pins of real gates,
    /// and every gate input pin must be driven exactly once.
    /// </summary>
    private void ValidateWiring()
    {
        foreach (var (load, driver) in _inputWiring)
        {
            var gate = LookupGate(load.GateId, "Wiring targets");
            if (!gate.InputPinNames.Contains(load.PinName))
                throw new ArgumentException(
                    $"Gate '{load.GateId}' has no input pin '{load.PinName}'. " +
                    $"Available inputs: {string.Join(", ", gate.InputPinNames)}.",
                    nameof(_inputWiring));
            ValidateDriver(driver);
        }

        foreach (var (gateId, gate) in Gates)
        {
            var undriven = gate.InputPinNames
                .Where(pin => !_inputWiring.ContainsKey(new LogicPinRef(gateId, pin)))
                .ToList();
            if (undriven.Count > 0)
                throw new ArgumentException(
                    $"Gate '{gateId}' has undriven input pins: {string.Join(", ", undriven)}. " +
                    "Every gate input must be wired exactly once.",
                    nameof(_inputWiring));
        }
    }

    /// <summary>Verifies one driver endpoint against the declared network inputs and gates.</summary>
    private void ValidateDriver(LogicNetDriver driver)
    {
        switch (driver)
        {
            case LogicNetDriver.NetworkInput input:
                if (!InputPinNames.Contains(input.PinName))
                    throw new ArgumentException(
                        $"The network has no input pin '{input.PinName}'. " +
                        $"Declared inputs: {string.Join(", ", InputPinNames)}.",
                        nameof(_inputWiring));
                break;
            case LogicNetDriver.GateOutput source:
                var gate = LookupGate(source.Pin.GateId, "Wiring drives from");
                if (!gate.OutputPinNames.Contains(source.Pin.PinName))
                    throw new ArgumentException(
                        $"Gate '{source.Pin.GateId}' has no output pin '{source.Pin.PinName}'. " +
                        $"Available outputs: {string.Join(", ", gate.OutputPinNames)}.",
                        nameof(_inputWiring));
                break;
            default:
                throw new ArgumentException(
                    $"Unsupported driver type '{driver.GetType().Name}'.", nameof(_inputWiring));
        }
    }

    /// <summary>Verifies every network output taps a real output pin of a real gate.</summary>
    private void ValidateOutputTaps()
    {
        if (_outputTaps.Count == 0)
            throw new ArgumentException("A logic network needs at least one output.", nameof(_outputTaps));
        foreach (var (outputName, tap) in _outputTaps)
        {
            var gate = LookupGate(tap.GateId, $"Network output '{outputName}' taps");
            if (!gate.OutputPinNames.Contains(tap.PinName))
                throw new ArgumentException(
                    $"Network output '{outputName}' taps pin '{tap.PinName}' of gate '{tap.GateId}', " +
                    $"which is not an output of that gate. Available outputs: {string.Join(", ", gate.OutputPinNames)}.",
                    nameof(_outputTaps));
        }
    }

    /// <summary>Returns the gate behind an id or throws a readable error naming the lookup context.</summary>
    private LogicGateModel LookupGate(string gateId, string context)
    {
        if (Gates.TryGetValue(gateId, out var gate))
            return gate;
        throw new ArgumentException(
            $"{context} unknown gate '{gateId}'. Known gates: {string.Join(", ", Gates.Keys)}.",
            nameof(_inputWiring));
    }

    /// <summary>Rejects missing or unknown network input bits before any gate fires.</summary>
    internal void ValidateInputBits(IReadOnlyDictionary<string, bool> inputBits)
    {
        var missing = InputPinNames.FirstOrDefault(name => !inputBits.ContainsKey(name));
        if (missing != null)
            throw new ArgumentException(
                $"No bit provided for network input '{missing}'. " +
                $"Required inputs: {string.Join(", ", InputPinNames)}.",
                nameof(inputBits));
        var unknown = inputBits.Keys.Except(InputPinNames).FirstOrDefault();
        if (unknown != null)
            throw new ArgumentException(
                $"The network has no input pin '{unknown}'. " +
                $"Declared inputs: {string.Join(", ", InputPinNames)}.",
                nameof(inputBits));
    }
}
