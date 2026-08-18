namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// One optical fan-out warning raised while assembling a logic network: a single
/// driver — a gate output pin wired to several gate inputs, or a network-input
/// signal shared by several unconnected gate inputs that carry the same pin name —
/// feeds more than one gate input. The logic layer hands every driven input the
/// full ideal level, but optically the waveguide behind the driver would have to
/// split, costing ~3 dB per branch (a 1×2 splitter halves the power), so a real
/// photonic implementation needs splitters plus level restoration (amplification
/// or a duplicated stage). The warning is non-blocking: the idealized logic result
/// stays available, and the network evaluates unchanged.
/// </summary>
/// <param name="DriverDisplayName">
/// The driver as shown to the user: <c>&lt;gate&gt;.&lt;pin&gt;</c> for a gate
/// output, or the shared pin name for a network-input signal (e.g. <c>A</c> for
/// the half adder's addend-A pins).
/// </param>
/// <param name="IsNetworkInputSignal">
/// True when the driver is a network-input signal (several unconnected gate
/// inputs sharing one pin name); false when it is a gate output pin.
/// </param>
/// <param name="LoadCount">How many gate inputs the driver feeds.</param>
/// <param name="LoadNames">
/// The driven gate inputs in <c>&lt;gate&gt;.&lt;pin&gt;</c> form, in declaration
/// order — listed in the UI so the user sees exactly which pins the warning
/// groups together.
/// </param>
/// <param name="Levels">
/// The quantitative level report (#1011): assuming an ideal 1×N splitter behind the
/// driver, the per-branch power and, per receiving input, whether it would still
/// reach that gate's power threshold and read as a logic 1. Advisory only — the
/// idealized logic result stays unchanged.
/// </param>
public sealed record LogicFanOutWarning(
    string DriverDisplayName,
    bool IsNetworkInputSignal,
    int LoadCount,
    IReadOnlyList<string> LoadNames,
    FanOutLevelReport Levels);
