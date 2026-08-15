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
}
