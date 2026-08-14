using CAP_Core.Components.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Canvas;

/// <summary>
/// ViewModel for a pin-less frozen waveguide path that lives directly on the canvas
/// (outside any <see cref="CAP_Core.Components.ComponentGroup"/>). Created when a
/// group containing GDS-imported route geometry is ungrouped (issue #856): the
/// pin-less paths have no components to re-attach to, so they are transferred here
/// instead of being discarded. Canvas-level paths render, move, delete and persist,
/// but never participate in simulation or routing.
/// </summary>
public partial class CanvasFrozenPathViewModel : ObservableObject
{
    /// <summary>The frozen path model with its fixed geometry.</summary>
    public FrozenWaveguidePath Path { get; }

    /// <summary>True while the path is the current canvas selection (rendered highlighted).</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Initializes the view-model around an existing frozen path.</summary>
    public CanvasFrozenPathViewModel(FrozenWaveguidePath path)
    {
        Path = path;
    }
}
