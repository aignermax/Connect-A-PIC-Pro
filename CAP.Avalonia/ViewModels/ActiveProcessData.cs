using System.Collections.Generic;
using CAP.Avalonia.ViewModels.Process;

namespace CAP.Avalonia.ViewModels;

/// <summary>
/// Serialisable form of the active process selection (issue #570).
/// Kept in its own file rather than inline next to <see cref="DesignFileData"/> to
/// respect the 250-line cap for new files (CLAUDE.md §1) — MainViewModel.cs is a
/// grandfathered exception.
/// </summary>
public class ActiveProcessData
{
    public string DisplayName { get; set; } = string.Empty;
    public bool IsPlayground { get; set; }
    public string? CoreMaterial { get; set; }
    public double? CoreThicknessNm { get; set; }
    public string? Cladding { get; set; }
    public int DesignWavelengthNm { get; set; } =
        CAP_Core.Components.Process.ProcessFingerprint.DefaultDesignWavelengthNm;
    public string? ProcessName { get; set; }
    public List<string> MemberPdkNames { get; set; } = new();

    /// <summary>
    /// Name of the PDK preset the process was picked from in the Fabrication Process window
    /// ("Load preset" = USE, issue #696). Null for processes chosen via the New-Design/startup
    /// picker and for legacy files, which load exactly as before.
    /// </summary>
    public string? PresetPdkName { get; set; }

    /// <summary>
    /// Design-specific property overrides on top of <see cref="PresetPdkName"/>'s process
    /// definition (issue #696). Null/empty means the preset is used as-is.
    /// </summary>
    public List<ProcessPropertyOverrideData>? Overrides { get; set; }
}
