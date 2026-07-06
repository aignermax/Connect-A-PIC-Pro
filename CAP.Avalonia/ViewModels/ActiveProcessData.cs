using System.Collections.Generic;

namespace CAP.Avalonia.ViewModels;

/// <summary>
/// Serialisable form of the active process selection (issue #570).
/// Kept in its own file rather than inline next to <see cref="DesignFileData"/> because
/// MainViewModel.cs already exceeds the 500-line guideline cap for new files.
/// </summary>
public class ActiveProcessData
{
    public string DisplayName { get; set; } = string.Empty;
    public bool IsPlayground { get; set; }
    public string? CoreMaterial { get; set; }
    public double? CoreThicknessNm { get; set; }
    public string? Cladding { get; set; }
    public int DesignWavelengthNm { get; set; } = 1550;
    public string? ProcessName { get; set; }
    public List<string> MemberPdkNames { get; set; } = new();
}
