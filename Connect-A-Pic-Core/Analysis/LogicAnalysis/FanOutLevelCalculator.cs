namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// The quantitative verdict for one receiving input of a fan-out site: the power
/// threshold the receiving gate's truth table was extracted at, and whether the
/// per-branch power after an ideal split would still reach it (power ≥ threshold
/// reads as logic 1 — the same contract the extraction classified the table by).
/// </summary>
/// <param name="LoadName">The driven gate input in <c>&lt;gate&gt;.&lt;pin&gt;</c> form.</param>
/// <param name="Threshold">Normalized power threshold of the receiving gate.</param>
/// <param name="ReadsAsOne">True when the per-branch power still reaches the threshold.</param>
public sealed record FanOutBranchLevel(string LoadName, double Threshold, bool ReadsAsOne);

/// <summary>
/// The quantitative fan-out level report for one site: an ideal 1×N splitter behind
/// the driver hands every branch <see cref="BranchPower"/> = P_out/N (a loss of
/// <see cref="SplitLossDb"/> = 10·log10(N) dB — conservative, no excess loss), and
/// every receiving input is checked against its gate's pinned threshold. Purely a
/// report: it changes nothing about the idealized logic evaluation.
/// </summary>
/// <param name="DriverPowerOne">
/// The normalized power the driver delivers at logic 1: the weakest 1-level of the
/// driving gate's truth table (conservative), or 1.0 for a network-input signal —
/// one source delivering the full power of one active input.
/// </param>
/// <param name="BranchPower">Per-branch power after the ideal split: P_out/N.</param>
/// <param name="SplitLossDb">Splitting loss in dB: 10·log10(N); 3.01 dB for N=2.</param>
/// <param name="Branches">One verdict per receiving input, in declaration order.</param>
public sealed record FanOutLevelReport(
    double DriverPowerOne,
    double BranchPower,
    double SplitLossDb,
    IReadOnlyList<FanOutBranchLevel> Branches);

/// <summary>
/// Computes the quantitative half of an optical fan-out warning (#1011): the
/// detection half already knows <em>where</em> a driver feeds several gate inputs;
/// this calculator answers whether the signal would still arrive as a logic 1 if
/// the driver were split ideally over its N consumers. The driving gate's logic-1
/// output power comes from its extracted truth table, the receiving gate's threshold
/// from the same persisted extraction — both exist since #984.
/// </summary>
public sealed class FanOutLevelCalculator
{
    /// <summary>Normalized power one network-input source delivers: the full power of one active input.</summary>
    public const double NetworkInputPowerOne = 1.0;

    private const int PowerToDbFactor = 10;

    private readonly IReadOnlyDictionary<string, LogicGateModel> _gates;

    /// <summary>Creates a calculator reading driver powers and thresholds from the network's gate models.</summary>
    public FanOutLevelCalculator(IReadOnlyDictionary<string, LogicGateModel> gates) =>
        _gates = gates ?? throw new ArgumentNullException(nameof(gates));

    /// <summary>Builds the level report for a gate-output fan-out site.</summary>
    /// <param name="driver">The driving gate output pin.</param>
    /// <param name="loads">The driven gate input pins (at least two — a single load never splits).</param>
    public FanOutLevelReport ForGateOutput(LogicPinRef driver, IReadOnlyList<LogicPinRef> loads) =>
        Build(WeakestOnePower(driver), loads);

    /// <summary>
    /// Builds the level report for a network-input fan-out site: the shared source
    /// (one laser) delivers the full input power, split ideally over the loads.
    /// </summary>
    /// <param name="loads">The driven gate input pins (at least two — a single load never splits).</param>
    public FanOutLevelReport ForNetworkInput(IReadOnlyList<LogicPinRef> loads) =>
        Build(NetworkInputPowerOne, loads);

    /// <summary>Per-branch power after an ideal 1×N split: P/N, no excess loss.</summary>
    public static double BranchPower(double driverPower, int loadCount)
    {
        if (loadCount < 1)
            throw new ArgumentOutOfRangeException(nameof(loadCount), loadCount,
                "A splitter needs at least one branch.");
        return driverPower / loadCount;
    }

    /// <summary>Splitting loss of an ideal 1×N splitter in dB: 10·log10(N) — 3.01 dB for N=2.</summary>
    public static double SplitLossDb(int loadCount)
    {
        if (loadCount < 1)
            throw new ArgumentOutOfRangeException(nameof(loadCount), loadCount,
                "A splitter needs at least one branch.");
        return PowerToDbFactor * Math.Log10(loadCount);
    }

    private FanOutLevelReport Build(double driverPowerOne, IReadOnlyList<LogicPinRef> loads)
    {
        if (loads == null) throw new ArgumentNullException(nameof(loads));
        if (loads.Count < 2)
            throw new ArgumentException(
                "A fan-out level report needs at least two loads — a single load never splits.",
                nameof(loads));

        var branchPower = BranchPower(driverPowerOne, loads.Count);
        var branches = loads.Select(load =>
        {
            var threshold = ReceivingThreshold(load);
            return new FanOutBranchLevel(FormatPin(load), threshold, branchPower >= threshold);
        }).ToList();
        return new FanOutLevelReport(driverPowerOne, branchPower, SplitLossDb(loads.Count), branches);
    }

    /// <summary>
    /// The weakest logic-1 level the driving gate emits on this output across its
    /// truth table — the conservative representative, so the verdict never promises
    /// more than the worst input combination delivers. 0 when no row reaches 1.
    /// </summary>
    private double WeakestOnePower(LogicPinRef driver)
    {
        var table = _gates[driver.GateId].TruthTable;
        var onePowers = table.Rows
            .Select(row => row.Outputs[driver.PinName])
            .Where(value => value.IsOne)
            .Select(value => value.Power)
            .ToList();
        return onePowers.Count == 0 ? 0.0 : onePowers.Min();
    }

    /// <summary>The power threshold the receiving gate's truth table was extracted at.</summary>
    private double ReceivingThreshold(LogicPinRef load) =>
        _gates[load.GateId].TruthTable.PowerThreshold;

    /// <summary>Renders a gate pin the way network inputs and taps are named: <c>gate.pin</c>.</summary>
    private static string FormatPin(LogicPinRef pin) => $"{pin.GateId}.{pin.PinName}";
}
