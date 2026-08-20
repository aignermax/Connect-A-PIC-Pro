using CAP_Core.Components.Core;

namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// The role each external pin of a gate group plays in a logic network: which pins
/// are logic inputs, which are logic outputs, which are constantly-on bias pins,
/// and the power threshold the gate's truth table was extracted at. Until role
/// persistence (#981) lands, callers and tests construct this record directly;
/// afterwards its data feeds this parameter.
/// </summary>
/// <param name="InputPinNames">External pin names driven as logic inputs.</param>
/// <param name="OutputPinNames">External pin names observed as logic outputs.</param>
/// <param name="BiasPinNames">External pin names held constantly "on" — they take no part in wiring.</param>
/// <param name="PowerThreshold">Normalized power threshold the gate's truth table was extracted at.</param>
/// <param name="InputSignalNames">
/// Optional network-signal name per input pin (issue #1025): unconnected input pins
/// carrying the same signal name merge into one network-level input; pins without an
/// entry keep their own <c>&lt;gate&gt;.&lt;pin&gt;</c> name. Null when unused.
/// </param>
/// <param name="OutputSignalNames">
/// Optional signal name per output pin: the pin's network-level output tap carries
/// the signal name instead of the raw <c>&lt;gate&gt;.&lt;pin&gt;</c> id — the
/// adder's sum reads <c>S</c>, its carry <c>Cout</c>. Names never merge (every tap
/// is one gate output) and must be unique across the network. Null when unused.
/// </param>
/// <param name="IsRegister">
/// Designates the gate as a behavioral register state element: its outputs hold
/// their last committed value during combinational settling, its inputs are sampled
/// and committed only on an explicit clock step, and a feedback cycle through it is
/// legal. See <see cref="TruthTablePinAssignment.IsRegister"/>.
/// </param>
public sealed record GateRoleAssignment(
    IReadOnlyList<string> InputPinNames,
    IReadOnlyList<string> OutputPinNames,
    IReadOnlyList<string> BiasPinNames,
    double PowerThreshold,
    IReadOnlyDictionary<string, string>? InputSignalNames = null,
    IReadOnlyDictionary<string, string>? OutputSignalNames = null,
    bool IsRegister = false);

/// <summary>
/// One top-level gate group on the canvas together with its logic-level model and
/// its role assignment — the unit of input a <see cref="LogicNetworkBuilder"/>
/// derives a network from.
/// </summary>
/// <param name="Group">The placed gate group; its name becomes the network-local gate id.</param>
/// <param name="Model">The evaluable logic model extracted from the group.</param>
/// <param name="Roles">The pin roles matching the extraction the model came from.</param>
public sealed record LogicGateInstance(
    ComponentGroup Group,
    LogicGateModel Model,
    GateRoleAssignment Roles);
