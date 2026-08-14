using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.Services.Localization;

namespace CAP.Avalonia.ViewModels.GdsImport;

/// <summary>
/// The result-summary half of <see cref="GdsImportDialogViewModel"/>: builds the
/// one-line outcome text shown in the dialog's result view, split out to keep
/// the main file under the project's 500-line gate.
/// </summary>
public partial class GdsImportDialogViewModel
{
    private static string BuildSummary(GdsPlacementReport report)
    {
        var summary = string.Format(
            LocalizationService.Instance.Translate("GdsImport.ResultSummary"),
            report.PlacedCount, report.ConnectedCount);
        if (report.RouteDerivedCount > 0 || report.FrozenRoutePathCount > 0)
        {
            summary += string.Format(
                LocalizationService.Instance.Translate("GdsImport.ResultRouteReconstructionSuffix"),
                report.RouteDerivedCount, report.FrozenRoutePathCount);
        }
        if (report.ReroutedCount > 0)
        {
            summary += string.Format(
                LocalizationService.Instance.Translate("GdsImport.ResultReroutedSuffix"),
                report.ReroutedCount);
        }
        if (report.GroupCreated)
        {
            summary += string.Format(
                LocalizationService.Instance.Translate("GdsImport.ResultGroupSuffix"), report.GroupName);
        }
        return summary;
    }
}
