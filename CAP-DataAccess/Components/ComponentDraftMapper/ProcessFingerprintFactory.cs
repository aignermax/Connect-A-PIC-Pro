using System;
using System.Linq;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP_DataAccess.Components.ComponentDraftMapper;

/// <summary>
/// Builds a <see cref="ProcessFingerprint"/> from a loaded <see cref="PdkDraft"/> (issue #570).
/// Core/cladding come from the process materials' <c>Role</c>; wavelength from the PDK default;
/// thickness from the process. A PDK without a process block yields an unspecified fingerprint.
/// </summary>
public static class ProcessFingerprintFactory
{
    /// <summary>Extracts the process fingerprint for the given PDK.</summary>
    public static ProcessFingerprint From(PdkDraft draft)
    {
        var process = draft.Process;
        var core = MaterialByRole(process, "core");
        var cladding = MaterialByRole(process, "cladding");

        return new ProcessFingerprint(
            CoreMaterial: core,
            CoreThicknessNm: process?.CoreThicknessNm,
            Cladding: cladding,
            DesignWavelengthNm: draft.DefaultWavelengthNm,
            ProcessName: string.IsNullOrWhiteSpace(process?.Name) ? null : process!.Name);
    }

    private static string? MaterialByRole(ProcessDefinition? process, string role) =>
        process?.Materials
            .FirstOrDefault(m => string.Equals(m.Role, role, StringComparison.OrdinalIgnoreCase))
            ?.Name;
}
