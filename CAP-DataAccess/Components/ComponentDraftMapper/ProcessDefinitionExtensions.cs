using CAP_Core.Analysis;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP_DataAccess.Components.ComponentDraftMapper;

/// <summary>
/// Extension methods for <see cref="ProcessDefinition"/>.
/// </summary>
public static class ProcessDefinitionExtensions
{
    /// <summary>
    /// Returns the process' minimum waveguide edge-to-edge spacing in micrometers,
    /// falling back to the conservative default when the PDK does not declare one.
    /// </summary>
    /// <param name="process">The active process definition (may be null).</param>
    /// <returns>
    /// <see cref="CAP_Core.PhotonicConstants.DefaultMinWaveguideSpacingMicrometers"/> when
    /// <paramref name="process"/> is null or does not declare a value; otherwise the declared value.
    /// </returns>
    public static double GetMinWaveguideSpacingMicrometersOrDefault(this ProcessDefinition? process)
    {
        return process?.MinWaveguideSpacingUm ?? CAP_Core.PhotonicConstants.DefaultMinWaveguideSpacingMicrometers;
    }

    /// <summary>
    /// Builds the DRC-lite min-width rules of the process: one
    /// <see cref="WaveguideMinWidthRule"/> per optical cross-section that declares
    /// <c>minWidthUm</c>, with its layer names resolved to GDS layer numbers via the
    /// process layer stack (the same resolution
    /// <see cref="ProcessOpticalDefaultsResolver"/> stamps onto pins). Metal
    /// cross-sections, cross-sections without a declared minimum, and cross-sections
    /// whose layers are all unknown to the stack are skipped — no fallback values are
    /// invented, so a PDK that declares nothing yields an empty list and the rule
    /// stays silent.
    /// </summary>
    /// <param name="process">The active process definition (may be null).</param>
    /// <returns>The declared min-width rules, empty when the process declares none.</returns>
    public static IReadOnlyList<WaveguideMinWidthRule> GetMinWaveguideWidthRules(this ProcessDefinition? process)
    {
        var rules = new List<WaveguideMinWidthRule>();
        if (process?.Xsections is null)
            return rules;

        foreach (var xsection in process.Xsections)
        {
            if (xsection.Kind != XsectionKind.Optical)
                continue;
            if (xsection.MinWidthUm is not > 0)
                continue;

            var layers = ResolveLayerNumbers(process, xsection);
            if (layers.Count == 0)
                continue;

            rules.Add(new WaveguideMinWidthRule(
                xsection.MinWidthUm.Value, layers, xsection.Name, xsection.DrcSource));
        }

        return rules;
    }

    private static List<int> ResolveLayerNumbers(ProcessDefinition process, ProcessXsection xsection)
    {
        var numbers = new List<int>();
        foreach (var layerName in xsection.Layers)
        {
            var layer = process.Layers?.FirstOrDefault(
                l => string.Equals(l.Name, layerName, StringComparison.OrdinalIgnoreCase));
            if (layer is not null && !numbers.Contains(layer.Layer))
                numbers.Add(layer.Layer);
        }

        return numbers;
    }
}
