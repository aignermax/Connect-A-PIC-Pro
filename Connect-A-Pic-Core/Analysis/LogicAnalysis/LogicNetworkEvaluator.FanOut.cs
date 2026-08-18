namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// Optical fan-out detection half of <see cref="LogicNetworkEvaluator"/>: walks the
/// validated wiring once at construction and reports every driver that feeds more
/// than one gate input. Optically, splitting a waveguide divides the optical power
/// (~3 dB per 1×2 branch), so a fanned-out signal can fall below the receiving
/// gate's threshold — the logic layer's "every input reads the full level" hides
/// this. The warnings are advisory only; evaluation stays idealized.
/// </summary>
public sealed partial class LogicNetworkEvaluator
{
    /// <summary>
    /// Every driver that feeds more than one gate input: gate outputs wired to
    /// several loads, and network-input signals — unconnected gate inputs that
    /// share one pin name (e.g. the half adder's four <c>*.A</c> pins forming
    /// addend A) and therefore stand for one physical source whose light would
    /// have to be split. Empty for a purely point-to-point network.
    /// </summary>
    public IReadOnlyList<LogicFanOutWarning> FanOutWarnings { get; private set; }
        = Array.Empty<LogicFanOutWarning>();

    /// <summary>Runs the fan-out detection over the validated wiring; called once from the constructor.</summary>
    private void DetectFanOut()
    {
        var levels = new FanOutLevelCalculator(Gates);
        var warnings = new List<LogicFanOutWarning>();
        warnings.AddRange(DetectGateOutputFanOut(levels));
        warnings.AddRange(DetectNetworkInputSignalFanOut(levels));
        FanOutWarnings = warnings;
    }

    /// <summary>One warning per gate output pin wired to more than one gate input.</summary>
    private IEnumerable<LogicFanOutWarning> DetectGateOutputFanOut(FanOutLevelCalculator levels)
    {
        return _inputWiring
            .Where(pair => pair.Value is LogicNetDriver.GateOutput)
            .GroupBy(pair => ((LogicNetDriver.GateOutput)pair.Value).Pin)
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                var loads = group.Select(pair => pair.Key).ToList();
                return new LogicFanOutWarning(
                    DriverDisplayName: FormatPin(group.Key),
                    IsNetworkInputSignal: false,
                    LoadCount: loads.Count,
                    LoadNames: loads.Select(FormatPin).ToList(),
                    Levels: levels.ForGateOutput(group.Key, loads));
            });
    }

    /// <summary>
    /// One warning per network-input signal: unconnected gate inputs whose pin
    /// names coincide (the half adder's <c>NAND1A.A</c>, <c>NAND1B.A</c>,
    /// <c>NAND2.A</c>, <c>NAND5.A</c> all carry pin name <c>A</c>) are one logical
    /// signal the user drives from a single source — physically one laser whose
    /// light would have to be split across every pin in the group.
    /// </summary>
    private IEnumerable<LogicFanOutWarning> DetectNetworkInputSignalFanOut(FanOutLevelCalculator levels)
    {
        return _inputWiring
            .Where(pair => pair.Value is LogicNetDriver.NetworkInput)
            .GroupBy(pair => pair.Key.PinName)
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                var loads = group.Select(pair => pair.Key).ToList();
                return new LogicFanOutWarning(
                    DriverDisplayName: group.Key,
                    IsNetworkInputSignal: true,
                    LoadCount: loads.Count,
                    LoadNames: loads.Select(FormatPin).ToList(),
                    Levels: levels.ForNetworkInput(loads));
            });
    }

    /// <summary>Renders a gate pin the way network inputs and taps are named: <c>gate.pin</c>.</summary>
    private static string FormatPin(LogicPinRef pin) => $"{pin.GateId}.{pin.PinName}";
}
