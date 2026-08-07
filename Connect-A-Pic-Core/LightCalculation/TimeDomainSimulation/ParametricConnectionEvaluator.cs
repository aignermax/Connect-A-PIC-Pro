using System.Numerics;
using CAP_Core.Components.FormulaReading;

namespace CAP_Core.LightCalculation.TimeDomainSimulation;

/// <summary>
/// Distinguishes truly nonlinear connection formulas (those depending on live pin
/// fields) from merely parametric ones (slider-bound only) and pre-evaluates the
/// parametric formulas into concrete complex weights.
///
/// A slider-bound formula is a constant at simulation time — parameters do not
/// change during a run — so a circuit containing only such formulas is still
/// linear and eligible for the Phase-1 time-domain path.
/// </summary>
public static class ParametricConnectionEvaluator
{
    /// <summary>
    /// True when the formula depends on the live field vector: it is flagged as an
    /// inner-loop function or references at least one pin of the matrix. Slider-only
    /// formulas return false — they are constants for the duration of a run.
    /// </summary>
    public static bool IsTrulyNonLinear(SMatrix matrix, ConnectionFunction function) =>
        function.IsInnerLoopFunction
        || function.UsedParameterGuids.Any(matrix.PinReference.ContainsKey);

    /// <summary>Counts the connections whose formulas depend on live pin inputs.</summary>
    public static int CountTrulyNonLinear(SMatrix matrix) =>
        matrix.NonLinearConnections.Count(kv => IsTrulyNonLinear(matrix, kv.Value));

    /// <summary>
    /// Evaluates every parametric (slider-only) connection formula with the matrix's
    /// current slider values and writes the resulting constant weight into the
    /// S-matrix. Mirrors what the CW path does on its first recompute pass, so the
    /// impulse-response closure sees the same transfer values.
    /// </summary>
    /// <param name="matrix">System or component S-matrix to update in place.</param>
    public static void EvaluateParametricConnections(SMatrix matrix)
    {
        var transfers = new Dictionary<(Guid PinIdInflow, Guid PinIdOutflow), Complex>();
        foreach (var (key, function) in matrix.NonLinearConnections)
        {
            if (IsTrulyNonLinear(matrix, function))
                continue;
            var sliderValues = function.UsedParameterGuids
                .Where(matrix.SliderReference.ContainsKey)
                .Select(guid => (object)matrix.SliderReference[guid])
                .ToList();
            transfers[key] = function.CalcConnectionWeightAsync(sliderValues);
        }
        matrix.SetValues(transfers);
    }
}
