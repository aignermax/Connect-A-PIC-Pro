using System.Numerics;

namespace CAP_Core.LightCalculation.TimeDomainSimulation.CompactModels;

/// <summary>
/// Result of stepping a compact model over an entire trace: the outgoing
/// optical field per sample, the electrical output per sample, and the final
/// integrator state.
/// </summary>
/// <param name="OutgoingField">Outgoing complex field, one sample per timestep.</param>
/// <param name="ElectricalOutput">Electrical output, one sample per timestep.</param>
/// <param name="FinalState">Model state after the last timestep.</param>
public sealed record ActiveTraceResult(
    Complex[] OutgoingField, double[] ElectricalOutput, CompactModelState FinalState);

/// <summary>
/// Steps an <see cref="ICompactModel"/> across a full time trace, sample by
/// sample. This is the inner loop shared by the mixed-signal simulator and by
/// standalone model characterization (tests, parameter fitting).
/// </summary>
public static class ActiveComponentStepper
{
    /// <summary>
    /// Runs <paramref name="model"/> over <paramref name="sampleCount"/> timesteps.
    /// </summary>
    /// <param name="model">Compact model to step.</param>
    /// <param name="dt">Timestep in seconds.</param>
    /// <param name="sampleCount">Number of timesteps.</param>
    /// <param name="incidentField">
    /// Incident optical field per sample, or null for a dark component
    /// (e.g. a laser source). Length must equal <paramref name="sampleCount"/>.
    /// </param>
    /// <param name="electricalInput">
    /// Electrical drive per sample, or null for 0 A / 0 V drive.
    /// Length must equal <paramref name="sampleCount"/>.
    /// </param>
    /// <param name="initialState">
    /// Starting state; defaults to <see cref="ICompactModel.CreateInitialState"/>.
    /// </param>
    public static ActiveTraceResult StepOverTrace(
        ICompactModel model,
        double dt,
        int sampleCount,
        Complex[]? incidentField = null,
        double[]? electricalInput = null,
        CompactModelState? initialState = null)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));
        if (dt <= 0) throw new ArgumentOutOfRangeException(nameof(dt));
        if (sampleCount <= 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));
        ValidateLength(incidentField?.Length, sampleCount, nameof(incidentField));
        ValidateLength(electricalInput?.Length, sampleCount, nameof(electricalInput));

        var state = initialState ?? model.CreateInitialState();
        var outgoing = new Complex[sampleCount];
        var electrical = new double[sampleCount];

        for (int n = 0; n < sampleCount; n++)
        {
            Complex incident = incidentField?[n] ?? Complex.Zero;
            double drive = electricalInput?[n] ?? 0.0;
            var step = model.Step(dt, incident, state, drive);
            outgoing[n] = step.OutgoingField;
            electrical[n] = step.ElectricalOutput;
        }

        return new ActiveTraceResult(outgoing, electrical, state);
    }

    private static void ValidateLength(int? actual, int expected, string paramName)
    {
        if (actual.HasValue && actual.Value != expected)
            throw new ArgumentException(
                $"{paramName} must have exactly {expected} samples (got {actual.Value}).", paramName);
    }
}
