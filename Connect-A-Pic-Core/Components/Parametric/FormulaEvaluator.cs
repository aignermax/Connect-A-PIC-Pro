using System.Globalization;
using NCalc;
using NCalc.Handlers;

namespace CAP_Core.Components.Parametric
{
    /// <summary>
    /// Evaluates mathematical formula expressions with named parameters.
    /// Uses NCalc for expression parsing and evaluation.
    /// Supports standard math functions: sqrt, sin, cos, abs, pow, etc.
    /// </summary>
    public class FormulaEvaluator
    {
        /// <summary>
        /// Name of the constant Pi available in formulas.
        /// </summary>
        public const string PiConstantName = "pi";

        /// <summary>
        /// Evaluates a formula string with the given parameter values.
        /// </summary>
        /// <param name="formula">The formula expression (e.g., "sqrt(coupling_ratio)").</param>
        /// <param name="parameters">Named parameter values to substitute.</param>
        /// <returns>The evaluated result as a double.</returns>
        public double Evaluate(string formula, IReadOnlyDictionary<string, double> parameters)
        {
            if (string.IsNullOrWhiteSpace(formula))
                throw new ArgumentException("Formula cannot be empty.", nameof(formula));

            // InvariantCulture pin: NCalc parses numeric literals via the
            // thread culture by default, so "0.707" would become 707 on
            // de-DE / fr-FR. Force invariant so formulas round-trip
            // identically regardless of where the app runs.
            var expression = new Expression(formula, ExpressionOptions.None, CultureInfo.InvariantCulture);

            expression.EvaluateParameter += (string name, ParameterEventArgs args) =>
            {
                if (name.Equals(PiConstantName, StringComparison.OrdinalIgnoreCase))
                {
                    args.Result = Math.PI;
                    return;
                }

                if (parameters.TryGetValue(name, out double value))
                {
                    args.Result = value;
                    return;
                }

                throw new InvalidOperationException(
                    $"Unknown parameter '{name}' in formula '{formula}'.");
            };

            var result = expression.Evaluate();
            return Convert.ToDouble(result);
        }

        /// <summary>
        /// Validates that a formula can be parsed and references only known parameters.
        /// </summary>
        /// <param name="formula">The formula to validate.</param>
        /// <param name="knownParameters">Set of valid parameter names.</param>
        /// <param name="error">Error message if validation fails.</param>
        /// <returns>True if the formula is valid.</returns>
        public bool TryValidate(
            string formula,
            IReadOnlyCollection<string> knownParameters,
            out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(formula))
            {
                error = "Formula is empty.";
                return false;
            }

            try
            {
                var testParams = new Dictionary<string, double>();
                foreach (var param in knownParameters)
                {
                    testParams[param] = 1.0;
                }

                Evaluate(formula, testParams);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
