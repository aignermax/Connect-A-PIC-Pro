using CommunityToolkit.Mvvm.ComponentModel;
using CAP_Core.Components;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Core;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Simulation;

namespace CAP.Avalonia.ViewModels.Canvas;

public partial class ComponentViewModel : ObservableObject
{
    public Component Component { get; }

    /// <summary>
    /// Display name for UI - uses HumanReadableName if available, falls back to Name.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (Component is ComponentGroup group) return group.GroupName;
            return Component.HumanReadableName ?? Component.Name;
        }
    }

    /// <summary>
    /// The name of the template used to create this component.
    /// </summary>
    public string? TemplateName { get; set; }

    /// <summary>
    /// The PDK source of the template (e.g. "Built-in", "Demo PDK").
    /// </summary>
    public string? TemplatePdkSource { get; set; }

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// Whether this component is locked (cannot be moved, rotated, or deleted).
    /// </summary>
    public bool IsLocked => Component.IsLocked;

    /// <summary>
    /// Notifies that the lock state has changed.
    /// </summary>
    public void NotifyLockStateChanged() => OnPropertyChanged(nameof(IsLocked));

    /// <summary>
    /// Laser configuration for light source components (null for non-sources).
    /// </summary>
    public LaserConfig? LaserConfig { get; }

    /// <summary>
    /// Whether this component is a light source (see <see cref="LightSourceClassifier"/>).
    /// </summary>
    public bool IsLightSource => LaserConfig != null;

    /// <summary>
    /// Light source whose laser is switched off — a listen-only output coupler (#690).
    /// The single predicate every simulation consumer must use to skip the source.
    /// </summary>
    public bool IsLaserOff => LaserConfig is { IsEnabled: false };

    public double Width => Component.WidthMicrometers;
    public double Height => Component.HeightMicrometers;
    public string Name => Component.HumanReadableName ?? Component.Identifier;

    /// <summary>
    /// Read-only component type label for display in the Properties panel.
    /// Returns the Nazca function name if available, otherwise the template name.
    /// Returns null for ComponentGroups (groups have no single type).
    /// </summary>
    public string? ComponentTypeName
    {
        get
        {
            if (Component is ComponentGroup) return null;
            if (!string.IsNullOrWhiteSpace(Component.NazcaFunctionName)) return Component.NazcaFunctionName;
            if (!string.IsNullOrWhiteSpace(TemplateName)) return TemplateName;

            var hint = !string.IsNullOrWhiteSpace(Component.Identifier)
                ? Component.Identifier
                : Component.HumanReadableName;

            if (string.IsNullOrWhiteSpace(hint)) return null;

            var normalizedHint = hint.ToLowerInvariant();

            if (normalizedHint.Contains("grating")) return "demo.io";
            if (normalizedHint.Contains("splitter") || normalizedHint.Contains("1x2")) return "demo.mmi1x2_sh";
            if (normalizedHint.Contains("coupler") || normalizedHint.Contains("2x2")) return "demo.mmi2x2_dp";
            if (normalizedHint.Contains("phase") || normalizedHint.Contains("shifter")) return "demo.eopm_dc";
            if (normalizedHint.Contains("detector") || normalizedHint.Contains("photo")) return "demo.pd";
            if (normalizedHint.Contains("bend")) return "demo.shallow.bend";
            if (normalizedHint.Contains("straight") || normalizedHint.Contains("waveguide")) return "demo.shallow.strt";
            if (normalizedHint.Contains("y-junction") || normalizedHint.Contains("yjunction")) return "demo.mmi1x2_sh";

            return "demo.shallow.strt";
        }
    }

    /// <summary>
    /// Whether this ComponentViewModel represents a ComponentGroup.
    /// </summary>
    public bool IsComponentGroup => Component is ComponentGroup;

    /// <summary>
    /// Whether this component has adjustable slider parameters.
    /// </summary>
    public bool HasSliders => Component.GetAllSliders().Count > 0;

    /// <summary>
    /// Display label for the slider.
    /// </summary>
    public string SliderLabel
    {
        get
        {
            if (TemplateName?.Contains("Phase") == true)
                return LocalizationService.Instance.Translate("Canvas.SliderLabel.Phase");
            if (TemplateName?.Contains("Directional") == true)
                return LocalizationService.Instance.Translate("Canvas.SliderLabel.Coupling");
            return LocalizationService.Instance.Translate("Canvas.SliderLabel.Parameter");
        }
    }

    /// <summary>
    /// Callback to notify the canvas that a slider changed.
    /// </summary>
    public Action? OnSliderChanged { get; set; }

    /// <summary>
    /// First slider's current value.
    /// </summary>
    public double SliderValue
    {
        get => Component.GetSlider(0)?.Value ?? 0;
        set
        {
            var slider = Component.GetSlider(0);
            if (slider != null && Math.Abs(slider.Value - value) > 0.001)
            {
                slider.Value = value;
                OnPropertyChanged();
                OnSliderChanged?.Invoke();
            }
        }
    }

    public double SliderMin => Component.GetSlider(0)?.MinValue ?? 0;
    public double SliderMax => Component.GetSlider(0)?.MaxValue ?? 1;

    public ComponentViewModel(Component component, string? templateName = null, string? templatePdkSource = null)
    {
        Component = component;
        TemplateName = templateName;
        TemplatePdkSource = templatePdkSource;
        _x = component.PhysicalX;
        _y = component.PhysicalY;

        if (LightSourceClassifier.IsLightInjectingCoupler(templateName)
            || LightSourceClassifier.IsLightInjectingCoupler(component))
            LaserConfig = new LaserConfig(component);
    }

    public void NotifyDimensionsChanged()
    {
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
    }
}
