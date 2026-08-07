using CAP.Avalonia.Services.Localization;
using CAP_DataAccess.Import.Gds.LayerCensus;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CAP.Avalonia.ViewModels.GdsImport;

/// <summary>
/// Which of the import dialog's three layer text fields a census click or an
/// accepted suggestion writes into.
/// </summary>
public enum GdsLayerFieldTarget
{
    /// <summary>The port-label layers field.</summary>
    PortLabels,

    /// <summary>The waveguide (optical) layers field.</summary>
    Waveguide,

    /// <summary>The metal (electrical) layers field.</summary>
    Metal,
}

/// <summary>
/// One suggestion chip of the import dialog: a labeled, user-confirmable guess
/// ("(1,10) → port labels — high confidence") the user accepts into a layer
/// field with a click. Nothing is prefilled silently: the fields only change
/// on an explicit accept, and an accepted chip shows a checkmark so applied
/// suggestions stay distinguishable from hand-entered values.
/// </summary>
public sealed partial class GdsLayerSuggestionChip : ObservableObject
{
    /// <summary>The suggestion behind this chip.</summary>
    public GdsLayerSuggestion Suggestion { get; }

    /// <summary>The field an accept writes into ("routing, kind unknown" targets the waveguide field).</summary>
    public GdsLayerFieldTarget TargetField { get; }

    /// <summary>Chip label, e.g. <c>(1,10) → port labels</c>.</summary>
    public string ChipText { get; }

    /// <summary>Provenance + confidence, shown as the chip's tooltip.</summary>
    public string Tooltip { get; }

    /// <summary>True while the target field contains the chip's pair (drives the checkmark).</summary>
    [ObservableProperty]
    private bool _isAccepted;

    /// <summary>Initializes a chip from one suggestion.</summary>
    public GdsLayerSuggestionChip(GdsLayerSuggestion suggestion)
    {
        Suggestion = suggestion ?? throw new ArgumentNullException(nameof(suggestion));
        TargetField = suggestion.Role switch
        {
            GdsLayerRole.PortLabels => GdsLayerFieldTarget.PortLabels,
            GdsLayerRole.Metal => GdsLayerFieldTarget.Metal,
            _ => GdsLayerFieldTarget.Waveguide,
        };
        ChipText = $"({suggestion.Layer},{suggestion.Datatype}) → {RoleText(suggestion.Role)}";
        Tooltip = string.Format(
            LocalizationService.Instance.Translate("GdsImport.SuggestionTooltipFormat"),
            suggestion.Reason, ConfidenceText(suggestion.Confidence));
    }

    private static string RoleText(GdsLayerRole role) => LocalizationService.Instance.Translate(role switch
    {
        GdsLayerRole.PortLabels => "GdsImport.SuggestionRolePortLabels",
        GdsLayerRole.Waveguide => "GdsImport.SuggestionRoleWaveguide",
        GdsLayerRole.Metal => "GdsImport.SuggestionRoleMetal",
        _ => "GdsImport.SuggestionRoleRoutingUnknown",
    });

    private static string ConfidenceText(GdsSuggestionConfidence confidence) =>
        LocalizationService.Instance.Translate(confidence switch
        {
            GdsSuggestionConfidence.High => "GdsImport.SuggestionConfidenceHigh",
            GdsSuggestionConfidence.Medium => "GdsImport.SuggestionConfidenceMedium",
            _ => "GdsImport.SuggestionConfidenceLow",
        });
}
