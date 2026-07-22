using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.Canvas;

/// <summary>
/// Design-wide designation of THE output coupler that the Eye/BER and Transient
/// analyses evaluate (issue #754). Lives on <see cref="DesignCanvasViewModel"/> as the
/// single source of truth for both analysis tabs, referencing the designated coupler
/// by its core <see cref="CAP_Core.Components.Core.Component.Id"/>. Persisted in the
/// design file by the component's Identifier (the Guid is regenerated on load).
/// </summary>
public partial class AnalysisOutputDesignation : ObservableObject
{
    /// <summary>
    /// Core <see cref="CAP_Core.Components.Core.Component.Id"/> of the designated
    /// output coupler, or null when no coupler is designated (automatic selection).
    /// </summary>
    [ObservableProperty]
    private Guid? _couplerId;

    /// <summary>True when an output coupler is currently designated.</summary>
    public bool HasDesignation => CouplerId != null;

    partial void OnCouplerIdChanged(Guid? value) => OnPropertyChanged(nameof(HasDesignation));

    /// <summary>Designates the coupler with the given core component id as the analysis output.</summary>
    /// <param name="couplerId">Core component id of the coupler.</param>
    public void Designate(Guid couplerId) => CouplerId = couplerId;

    /// <summary>Removes the designation; the analyses fall back to automatic selection.</summary>
    public void Clear() => CouplerId = null;
}
