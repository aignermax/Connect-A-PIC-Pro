using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Simulation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Properties.Editors;

/// <summary>
/// Editor ViewModel for a light-source component (grating coupler, edge
/// coupler). Exposes the per-instance <see cref="LaserConfig"/> so the user
/// can set this source's wavelength and input power without leaving the
/// canvas.
/// </summary>
public partial class LightSourceEditorViewModel : ObservableObject
{
    private readonly ComponentViewModel _componentVm;

    /// <summary>Display name of the underlying component.</summary>
    public string ComponentName => !string.IsNullOrEmpty(_componentVm.Component.HumanReadableName)
        ? _componentVm.Component.HumanReadableName!
        : _componentVm.Component.Name;

    /// <summary>The per-instance laser configuration bound by the panel.</summary>
    public LaserConfig? LaserConfig => _componentVm.LaserConfig;

    /// <summary>
    /// Discrete wavelengths the simulation supports. The editor offers only these
    /// (not a free numeric field) so a source can't be set to an unsupported wavelength.
    /// </summary>
    public IReadOnlyList<WavelengthOption> WavelengthOptions => WavelengthOption.All;

    /// <summary>
    /// Selectable spectral line shapes (Issue #819). "Ideal" keeps today's
    /// monochromatic behaviour; Gaussian/Lorentzian enable the linewidth field.
    /// </summary>
    public IReadOnlyList<LineShapeOption> LineShapeOptions { get; } = LineShapeOption.CreateAll();

    /// <summary>Creates the editor for the given light-source component.</summary>
    public LightSourceEditorViewModel(ComponentViewModel componentVm)
    {
        _componentVm = componentVm;
    }
}

/// <summary>Provider that surfaces the laser editor for components with a <see cref="LaserConfig"/>.</summary>
public class LightSourceEditorProvider : IComponentEditorProvider
{
    /// <inheritdoc/>
    public object? TryCreateEditor(ComponentViewModel componentVm)
    {
        if (componentVm.Component.IsAnalysisTool) return null;
        // Match the same NazcaFunctionName-based predicate the L-key simulation
        // uses (SimulationService.IsLightSource) so PDK-imported couplers with
        // GUID-based identifiers (e.g. ebeam_gc_te1550) still surface this
        // editor — not just templates whose display name literally equals
        // "Grating Coupler" / "Edge Coupler".
        if (!SimulationService.IsLightSource(componentVm.Component)) return null;
        if (componentVm.LaserConfig == null) return null;
        return new LightSourceEditorViewModel(componentVm);
    }
}
