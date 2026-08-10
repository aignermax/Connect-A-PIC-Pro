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
/// suggestions stay distinguishable from hand-entered values. Accepting is
/// reversible — clicking an accepted chip again removes its pair. Chips whose
/// role is undecidable ("routing, kind unknown") are not acceptable at all:
/// they inform, but the layer is assigned deliberately via a census-row click.
/// </summary>
public sealed partial class GdsLayerSuggestionChip : ObservableObject
{
    /// <summary>The suggestion behind this chip.</summary>
    public GdsLayerSuggestion Suggestion { get; }

    /// <summary>The field an accept writes into ("routing, kind unknown" targets the waveguide field).</summary>
    public GdsLayerFieldTarget TargetField { get; }

    /// <summary>False for "routing, kind unknown" — undecidable suggestions inform but cannot be accepted.</summary>
    public bool IsAcceptable { get; }

    /// <summary>Chip label, e.g. <c>(1,10) → port labels</c>.</summary>
    public string ChipText { get; }

    /// <summary>
    /// Provenance + confidence, shown as the chip's tooltip — plus a toggle hint
    /// while accepted, or a deliberate-assignment hint when not acceptable.
    /// </summary>
    public string Tooltip
    {
        get
        {
            var text = string.Format(
                LocalizationService.Instance.Translate("GdsImport.SuggestionTooltipFormat"),
                Suggestion.Reason, ConfidenceText(Suggestion.Confidence));
            if (!IsAcceptable)
                return text + " " + LocalizationService.Instance.Translate("GdsImport.SuggestionUnknownHint");
            if (IsAccepted)
                return text + " " + LocalizationService.Instance.Translate("GdsImport.SuggestionAcceptedHint");
            return text;
        }
    }

    /// <summary>True while the target field contains the chip's pair (drives the checkmark).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private bool _isAccepted;

    /// <summary>Initializes a chip from one suggestion.</summary>
    public GdsLayerSuggestionChip(GdsLayerSuggestion suggestion)
    {
        Suggestion = suggestion ?? throw new ArgumentNullException(nameof(suggestion));
        IsAcceptable = suggestion.Role != GdsLayerRole.RoutingUnknown;
        TargetField = suggestion.Role switch
        {
            GdsLayerRole.PortLabels => GdsLayerFieldTarget.PortLabels,
            GdsLayerRole.Metal => GdsLayerFieldTarget.Metal,
            _ => GdsLayerFieldTarget.Waveguide,
        };
        // No arrow for undecidable suggestions: the arrow implies an assignment
        // target the chip deliberately does not offer.
        ChipText = IsAcceptable
            ? $"({suggestion.Layer},{suggestion.Datatype}) → {RoleText(suggestion.Role)}"
            : $"({suggestion.Layer},{suggestion.Datatype}) — {RoleText(suggestion.Role)}";
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
