using System.Collections.ObjectModel;
using CAP_Core.Export.PdkResolution;

namespace CAP.Avalonia.ViewModels.PdkResolution;

/// <summary>
/// One row in the PDK resolution report — a single component's export function
/// (<c>nazcaFunction</c> or, for gdsfactory-native PDKs, <c>gdsFactoryFunction</c>)
/// and how it resolved against the installed Python packages (issue #515).
/// </summary>
public class PdkResolutionRowViewModel
{
    /// <summary>Component display name from the PDK JSON.</summary>
    public string ComponentName { get; init; } = "";

    /// <summary>The export-function string checked — the component's <c>nazcaFunction</c>,
    /// or its <c>gdsFactoryFunction</c> for gdsfactory-native components.</summary>
    public string FunctionPath { get; init; } = "";

    /// <summary>Resolution status.</summary>
    public PdkResolutionStatus Status { get; init; }

    /// <summary>Resolution detail (resolution path or error text).</summary>
    public string Message { get; init; } = "";

    /// <summary>Status badge glyph for the table.</summary>
    public string StatusBadge => Status switch
    {
        PdkResolutionStatus.Ok => "✅",
        PdkResolutionStatus.Warning => "⚠️",
        _ => "❌"
    };

    /// <summary>Foreground color hex for the message text.</summary>
    public string StatusColor => Status switch
    {
        PdkResolutionStatus.Ok => "#aaffaa",
        PdkResolutionStatus.Warning => "#ffddaa",
        _ => "#ff9999"
    };

    /// <summary>True when this row is a dead reference or warning (for the copy list).</summary>
    public bool IsFailure => Status != PdkResolutionStatus.Ok;
}

/// <summary>Per-PDK-file group of resolution rows.</summary>
public class PdkResolutionGroupViewModel
{
    /// <summary>PDK display name from the JSON (or the file name when loading failed).</summary>
    public string PdkName { get; init; } = "";

    /// <summary>JSON file name (e.g. "demo-pdk.json").</summary>
    public string FileName { get; init; } = "";

    /// <summary>Load or run-level error for this PDK; null when rows are populated.</summary>
    public string? Error { get; init; }

    /// <summary>True when <see cref="Error"/> is set — controls the error banner in the dialog.</summary>
    public bool HasError => Error != null;

    /// <summary>Resolution rows, one per component.</summary>
    public ObservableCollection<PdkResolutionRowViewModel> Rows { get; } = new();

    /// <summary>Summary line, e.g. "12 ok, 1 warning, 2 errors".</summary>
    public string Summary
    {
        get
        {
            var ok = Rows.Count(r => r.Status == PdkResolutionStatus.Ok);
            var warn = Rows.Count(r => r.Status == PdkResolutionStatus.Warning);
            var err = Rows.Count(r => r.Status == PdkResolutionStatus.Error);
            return $"{ok} ok, {warn} warnings, {err} errors";
        }
    }
}
