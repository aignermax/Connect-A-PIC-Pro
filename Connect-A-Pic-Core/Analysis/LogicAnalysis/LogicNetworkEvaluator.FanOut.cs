namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// Fan-out reporting half of <see cref="LogicNetworkEvaluator"/>: fan-out of a driver to
/// several loads is allowed (the evaluation stays available), but it is no longer silent —
/// every gate output and every network input feeding more than one gate input is reported
/// as a <see cref="LogicFanOutWarning"/>. Optically such a fork needs a splitter per branch
/// (~3 dB each) plus level restoration, which the idealized logic layer hands out for free.
/// </summary>
public sealed partial class LogicNetworkEvaluator
{
    /// <summary>
    /// Every driver that fans out to more than one gate input, ordered by pin name.
    /// Empty for a purely point-to-point network.
    /// </summary>
    public IReadOnlyList<LogicFanOutWarning> FanOutWarnings { get; private set; }
        = Array.Empty<LogicFanOutWarning>();

    /// <summary>Groups the wiring by driver and reports every driver feeding more than one load.</summary>
    private static IReadOnlyList<LogicFanOutWarning> ComputeFanOutWarnings(
        IReadOnlyDictionary<LogicPinRef, LogicNetDriver> inputWiring) =>
        inputWiring
            .GroupBy(pair => pair.Value)
            .Where(group => group.Count() > 1)
            .Select(group => new LogicFanOutWarning(DriverName(group.Key), group.Count()))
            .OrderBy(warning => warning.PinName, StringComparer.Ordinal)
            .ToList();

    /// <summary>Renders a driver the way network pins are named everywhere else.</summary>
    private static string DriverName(LogicNetDriver driver) =>
        driver switch
        {
            LogicNetDriver.NetworkInput input => input.PinName,
            LogicNetDriver.GateOutput source => $"{source.Pin.GateId}.{source.Pin.PinName}",
            _ => throw new InvalidOperationException($"Unsupported driver type '{driver.GetType().Name}'."),
        };
}
