namespace CAP.Avalonia.Controls.Rendering.LabelDeclutter;

/// <summary>
/// Ranks a canvas label's claim to contested screen space when two labels overlap.
/// Higher values win: a selected component's name label always survives an overlap with a
/// merely hovered or ordinary one, and a hovered one survives against an ordinary one.
/// </summary>
public enum LabelPriority
{
    /// <summary>Neither selected nor hovered — the common case, lowest claim on the space.</summary>
    Normal = 0,

    /// <summary>The pointer is currently over this label's owner.</summary>
    Hovered = 1,

    /// <summary>The label's owner is part of the current selection.</summary>
    Selected = 2,
}
