using System.Numerics;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.ExternalPorts;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;

namespace CAP_Core.Analysis.LogicAnalysis;

/// <summary>
/// Extracts a digital truth table from a grouped photonic circuit by running the
/// existing S-matrix simulation (<see cref="GridLightCalculator"/>) once per binary
/// input combination: an "on" logic input means coherent laser light (unit amplitude,
/// zero phase) is injected at that group pin, and an output counts as logic 1 when
/// its normalized optical power reaches the caller-supplied threshold. All "on"
/// inputs of one combination are simulated together — coherent, exactly like the
/// regular field simulation — so interference shows up in the raw power values.
/// The group is always simulated in isolation; canvas connections around the group
/// are irrelevant for its gate behavior.
///
/// Boundaries: at most <see cref="MaxLogicInputs"/> logic inputs (the combination
/// count grows as 2^n and each combination is a full simulation run), the power
/// threshold is a mandatory parameter (no guessing), and the whole table is
/// extracted at one laser wavelength.
/// </summary>
public sealed class TruthTableExtractor
{
    /// <summary>
    /// Maximum number of logic inputs: 4 inputs produce 16 simulation runs, which
    /// stays fast enough for interactive use and covers the gates of the NAND game.
    /// </summary>
    public const int MaxLogicInputs = 4;

    /// <summary>In-flow field of an "on" input: unit amplitude, zero phase — coherent across all on inputs.</summary>
    private static readonly Complex OnInputField = new(1.0, 0.0);

    /// <summary>
    /// Simulates every binary input combination of <paramref name="group"/> and
    /// classifies each output against <paramref name="powerThreshold"/>.
    /// </summary>
    /// <param name="group">The grouped circuit under test; its external pins form the gate interface.</param>
    /// <param name="inputPinNames">
    /// Names of the group's external pins to drive as logic inputs
    /// (1 to <see cref="MaxLogicInputs"/> entries, no duplicates, disjoint from the outputs).
    /// </param>
    /// <param name="outputPinNames">Names of the group's external pins to observe as logic outputs.</param>
    /// <param name="powerThreshold">
    /// Normalized power threshold in the open interval (0, 1): an output is logic 1 when
    /// its power is ≥ threshold. Each active input injects power 1.
    /// </param>
    /// <param name="wavelengthNm">Laser wavelength in nm shared by all inputs (the active laser).</param>
    /// <param name="cancellationToken">Cancels the extraction between combinations.</param>
    /// <returns>The truth table, including the raw simulated power behind every output bit.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// A pin list is empty, contains duplicates, overlaps the other list, exceeds
    /// <see cref="MaxLogicInputs"/> inputs, or names a pin the group does not expose.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="powerThreshold"/> lies outside (0, 1), or <paramref name="wavelengthNm"/> is not positive.
    /// </exception>
    public async Task<TruthTable> ExtractAsync(
        ComponentGroup group,
        IReadOnlyList<string> inputPinNames,
        IReadOnlyList<string> outputPinNames,
        double powerThreshold,
        int wavelengthNm,
        CancellationToken cancellationToken = default)
    {
        if (group == null) throw new ArgumentNullException(nameof(group));
        ValidateArguments(inputPinNames, outputPinNames, powerThreshold, wavelengthNm);
        var inputs = ResolvePins(group, inputPinNames, nameof(inputPinNames));
        var outputs = ResolvePins(group, outputPinNames, nameof(outputPinNames));

        // Compute the group closure once up front: a group that cannot simulate at
        // all fails before the first row, and the per-combination runs reuse it.
        group.EnsureSMatrixComputed();

        var rowCount = 1 << inputs.Count;
        var rows = new List<TruthTableRow>(rowCount);
        for (var pattern = 0; pattern < rowCount; pattern++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(await SimulateCombinationAsync(
                group, inputs, outputs, pattern, powerThreshold, wavelengthNm, cancellationToken));
        }

        return new TruthTable(
            group.GroupName,
            inputPinNames.ToArray(),
            outputPinNames.ToArray(),
            powerThreshold,
            wavelengthNm,
            rows);
    }

    /// <summary>Simulates one input bit pattern and classifies every declared output pin.</summary>
    private static async Task<TruthTableRow> SimulateCombinationAsync(
        ComponentGroup group,
        IReadOnlyList<GroupPin> inputs,
        IReadOnlyList<GroupPin> outputs,
        int pattern,
        double powerThreshold,
        int wavelengthNm,
        CancellationToken cancellationToken)
    {
        var portManager = new PhysicalExternalPortManager();
        var inputBits = new Dictionary<string, bool>(inputs.Count);
        for (var i = 0; i < inputs.Count; i++)
        {
            var isOn = (pattern & (1 << i)) != 0;
            inputBits[inputs[i].Name] = isOn;
            if (isOn)
            {
                portManager.AddLightSource(CreateOnInput(inputs[i], wavelengthNm), InFlowIdOf(inputs[i]));
            }
        }

        var fields = await RunSimulationAsync(group, portManager, wavelengthNm, cancellationToken);

        var outputValues = new Dictionary<string, LogicOutputValue>(outputs.Count);
        foreach (var output in outputs)
        {
            var power = OutputPower(fields, output);
            outputValues[output.Name] = new LogicOutputValue(power >= powerThreshold, power);
        }

        return new TruthTableRow(inputBits, outputValues);
    }

    /// <summary>Runs the flat S-matrix field propagation over the group with the given light sources.</summary>
    private static async Task<Dictionary<Guid, Complex>> RunSimulationAsync(
        ComponentGroup group,
        PhysicalExternalPortManager portManager,
        int wavelengthNm,
        CancellationToken cancellationToken)
    {
        var tileManager = new ComponentListTileManager();
        tileManager.AddComponent(group);
        var connectionManager = new WaveguideConnectionManager(new WaveguideRouter());
        var grid = GridManager.CreateForSimulation(tileManager, connectionManager, portManager);
        var calculator = new GridLightCalculator(new SystemMatrixBuilder(grid), grid);
        using var cancelSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var fields = await calculator.CalculateFieldPropagationAsync(cancelSource, wavelengthNm);

        // The solver maps cancellation to an empty result; honor the caller's token instead.
        cancellationToken.ThrowIfCancellationRequested();
        return fields;
    }

    /// <summary>Creates the coherent laser input for one "on" pin at the shared wavelength.</summary>
    private static ExternalInput CreateOnInput(GroupPin pin, int wavelengthNm) =>
        new(pin.Name, new LaserType(LightColor.Red, wavelengthNm), 0, OnInputField, true);

    /// <summary>The flow ID light is injected at: the in-flow of the pin's internal component pin.</summary>
    private static Guid InFlowIdOf(GroupPin pin) => pin.InternalPin!.LogicalPin!.IDInFlow;

    /// <summary>
    /// Normalized power leaving the group through <paramref name="pin"/> — the squared
    /// field magnitude at the out-flow. A pin flow missing from the simulated field
    /// map carries no light and therefore reads as 0.
    /// </summary>
    private static double OutputPower(Dictionary<Guid, Complex> fields, GroupPin pin)
    {
        var outFlowId = pin.InternalPin!.LogicalPin!.IDOutFlow;
        return fields.TryGetValue(outFlowId, out var field) ? field.Magnitude * field.Magnitude : 0.0;
    }

    /// <summary>Checks the pure list/threshold invariants that need no group lookup.</summary>
    private static void ValidateArguments(
        IReadOnlyList<string> inputPinNames,
        IReadOnlyList<string> outputPinNames,
        double powerThreshold,
        int wavelengthNm)
    {
        if (inputPinNames == null || inputPinNames.Count == 0)
            throw new ArgumentException("At least one logic input pin is required.", nameof(inputPinNames));
        if (inputPinNames.Count > MaxLogicInputs)
            throw new ArgumentException(
                $"At most {MaxLogicInputs} logic inputs are supported ({1 << MaxLogicInputs} combinations); got {inputPinNames.Count}.",
                nameof(inputPinNames));
        if (outputPinNames == null || outputPinNames.Count == 0)
            throw new ArgumentException("At least one logic output pin is required.", nameof(outputPinNames));

        ThrowOnDuplicates(inputPinNames, nameof(inputPinNames));
        ThrowOnDuplicates(outputPinNames, nameof(outputPinNames));
        var overlap = inputPinNames.Intersect(outputPinNames).FirstOrDefault();
        if (overlap != null)
            throw new ArgumentException(
                $"Pin '{overlap}' is declared as both a logic input and a logic output.", nameof(outputPinNames));

        if (double.IsNaN(powerThreshold) || powerThreshold <= 0.0 || powerThreshold >= 1.0)
            throw new ArgumentOutOfRangeException(
                nameof(powerThreshold), powerThreshold,
                "Normalized power threshold must lie in the open interval (0, 1).");
        if (wavelengthNm <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(wavelengthNm), wavelengthNm, "Wavelength must be a positive number of nanometers.");
    }

    /// <summary>Maps pin names onto the group's external pins, failing clearly on unknown or unsimulatable pins.</summary>
    private static List<GroupPin> ResolvePins(
        ComponentGroup group, IReadOnlyList<string> pinNames, string parameterName)
    {
        var pins = new List<GroupPin>(pinNames.Count);
        foreach (var name in pinNames)
        {
            var pin = group.ExternalPins.FirstOrDefault(p => p.Name == name);
            if (pin == null)
                throw new ArgumentException(
                    $"Group '{group.GroupName}' exposes no external pin named '{name}'. " +
                    $"Available pins: {string.Join(", ", group.ExternalPins.Select(p => p.Name))}.",
                    parameterName);
            if (pin.InternalPin?.LogicalPin == null)
                throw new ArgumentException(
                    $"External pin '{name}' of group '{group.GroupName}' is not bound to a simulatable component pin.",
                    parameterName);
            pins.Add(pin);
        }
        return pins;
    }

    /// <summary>Rejects duplicated pin names — one pin name, one logic wire.</summary>
    private static void ThrowOnDuplicates(IReadOnlyList<string> pinNames, string parameterName)
    {
        var duplicate = pinNames.GroupBy(n => n).FirstOrDefault(g => g.Count() > 1)?.Key;
        if (duplicate != null)
            throw new ArgumentException($"Pin '{duplicate}' is listed more than once.", parameterName);
    }
}
