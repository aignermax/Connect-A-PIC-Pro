using System.Collections.Generic;
using CAP.Avalonia.Services.AddCustomComponent;

namespace CAP.Avalonia.ViewModels.Components.AddCustomComponent;

/// <summary>
/// A user-facing labelling of an <see cref="SMatrixSource"/> choice for the New Component
/// window's S-matrix ComboBox (issue #701). Labels are deliberately honest about what each
/// choice means physically.
/// </summary>
/// <param name="Value">The underlying source the option stands for.</param>
/// <param name="Label">The text shown in the ComboBox.</param>
public sealed record SMatrixSourceOption(SMatrixSource Value, string Label)
{
    /// <summary>Renders the label, so a plain ComboBox shows readable text.</summary>
    public override string ToString() => Label;

    /// <summary>The options offered in the window, in display order (black box is the default).</summary>
    public static IReadOnlyList<SMatrixSourceOption> All { get; } = new[]
    {
        new SMatrixSourceOption(SMatrixSource.BlackBox, "Black box (no simulation model)"),
        new SMatrixSourceOption(SMatrixSource.Fdtd, "FDTD result (computed this session)"),
        new SMatrixSourceOption(SMatrixSource.LosslessTwoPort, "Ideal lossless pass-through (2-port routing only)"),
    };

    /// <summary>Looks up the option for <paramref name="source"/> from <see cref="All"/>.</summary>
    public static SMatrixSourceOption For(SMatrixSource source)
    {
        foreach (var option in All)
        {
            if (option.Value == source)
                return option;
        }
        return All[0];
    }
}
