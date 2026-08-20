using System.Globalization;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;

namespace CAP_Core.Analysis;

/// <summary>
/// DRC-lite rule: flags optical waveguide connections whose effective width falls
/// below the fabrication minimum (<c>minWidthUm</c>) of the associated cross-section
/// of the active process. The effective width is the narrowest fabricated feature
/// along the route — the connection width (what the export draws) and, when stamped
/// from the PDK, the endpoint pin widths. Association runs through the pins' GDS
/// layer; connections without layer information are skipped (no fallback guessing),
/// as are PDKs that declare no <c>minWidthUm</c>. Frozen group paths are out of
/// scope — they carry no pins to associate a cross-section with.
/// </summary>
public class WaveguideMinWidthChecker
{
    /// <summary>Float-noise guard so a width exactly at the minimum never fires.</summary>
    private const double WidthToleranceMicrometers = 1e-9;

    /// <summary>
    /// Checks every optical connection against the applicable cross-section rules.
    /// When several cross-sections share a layer, the smallest declared minimum
    /// governs — the check only flags widths no cross-section on that layer allows,
    /// so it never invents a violation the foundry would not.
    /// </summary>
    /// <param name="connections">The connections to check.</param>
    /// <param name="rules">Per-cross-section minimums of the active process.</param>
    /// <returns>One issue per violating connection, empty when all are compliant.</returns>
    public List<DesignIssue> CheckConnections(
        IEnumerable<WaveguideConnection> connections,
        IReadOnlyList<WaveguideMinWidthRule> rules)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(rules);

        var issues = new List<DesignIssue>();
        if (rules.Count == 0)
            return issues;

        foreach (var connection in connections)
        {
            var issue = CheckConnection(connection, rules);
            if (issue is not null)
                issues.Add(issue);
        }

        return issues;
    }

    /// <summary>
    /// Per-connection counterpart (issue #936): each connection is checked against the
    /// rule set of its OWN endpoint PDKs — resolved by the caller — instead of one
    /// design-wide list, so a Cornerstone chiplet on a multi-process canvas is checked
    /// against the Cornerstone minimum while a SiEPIC chiplet (no declared minimum)
    /// stays silent, even in Playground where no process lock exists. A connection
    /// whose rule set resolves to null or empty is skipped (no PDK opinion).
    /// </summary>
    /// <param name="connections">The connections to check.</param>
    /// <param name="rulesForConnection">
    /// Resolves the min-width rules governing one connection; null/empty return means
    /// "no declared minimum" and the connection is skipped.
    /// </param>
    /// <returns>One issue per violating connection, empty when all are compliant.</returns>
    public List<DesignIssue> CheckConnections(
        IEnumerable<WaveguideConnection> connections,
        Func<WaveguideConnection, IReadOnlyList<WaveguideMinWidthRule>?> rulesForConnection)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(rulesForConnection);

        var issues = new List<DesignIssue>();
        foreach (var connection in connections)
        {
            var rules = rulesForConnection(connection);
            if (rules is not { Count: > 0 })
                continue;

            var issue = CheckConnection(connection, rules);
            if (issue is not null)
                issues.Add(issue);
        }

        return issues;
    }

    private static DesignIssue? CheckConnection(
        WaveguideConnection connection,
        IReadOnlyList<WaveguideMinWidthRule> rules)
    {
        if (connection.IsElectrical)
            return null;
        if (connection.StartPin == null || connection.EndPin == null)
            return null;

        var rule = FindApplicableRule(connection, rules);
        if (rule is null)
            return null;

        double effectiveWidth = EffectiveWidth(connection);
        if (effectiveWidth >= rule.MinWidthMicrometers - WidthToleranceMicrometers)
            return null;

        var (startX, startY) = connection.StartPin.GetAbsolutePosition();
        var (endX, endY) = connection.EndPin.GetAbsolutePosition();
        string sourceSuffix = string.IsNullOrWhiteSpace(rule.DrcSource)
            ? string.Empty
            : $"; source: {rule.DrcSource}";
        string description = string.Create(
            CultureInfo.InvariantCulture,
            $"Waveguide width below minimum: {FormatPinName(connection.StartPin)} → {FormatPinName(connection.EndPin)} "
            + $"(width {effectiveWidth:F2} µm, minimum {rule.MinWidthMicrometers:F2} µm, "
            + $"cross-section {rule.XsectionName}{sourceSuffix})");

        return new DesignIssue(
            DesignIssueType.WaveguideBelowMinWidth,
            connection,
            (startX + endX) / 2,
            (startY + endY) / 2,
            description);
    }

    /// <summary>
    /// The smallest minimum among the rules whose layers cover either endpoint pin's
    /// layer; null when no rule applies (pins carry no layer, or their layer belongs
    /// to a cross-section without a declared minimum).
    /// </summary>
    private static WaveguideMinWidthRule? FindApplicableRule(
        WaveguideConnection connection,
        IReadOnlyList<WaveguideMinWidthRule> rules)
    {
        WaveguideMinWidthRule? applicable = null;
        foreach (var rule in rules)
        {
            if (!CoversPin(rule, connection.StartPin) && !CoversPin(rule, connection.EndPin))
                continue;
            if (applicable is null || rule.MinWidthMicrometers < applicable.MinWidthMicrometers)
                applicable = rule;
        }

        return applicable;
    }

    private static bool CoversPin(WaveguideMinWidthRule rule, PhysicalPin pin)
    {
        return pin.Layer.HasValue && rule.GdsLayers.Contains(pin.Layer.Value);
    }

    /// <summary>
    /// The narrowest fabricated feature along the route: the connection width (the
    /// geometry the export draws) plus any PDK-stamped endpoint pin widths.
    /// </summary>
    private static double EffectiveWidth(WaveguideConnection connection)
    {
        double width = connection.WidthMicrometers;
        if (connection.StartPin.WaveguideWidthMicrometers is double startWidth && startWidth < width)
            width = startWidth;
        if (connection.EndPin.WaveguideWidthMicrometers is double endWidth && endWidth < width)
            width = endWidth;
        return width;
    }

    private static string FormatPinName(PhysicalPin pin)
    {
        return $"{pin.ParentComponent.Identifier}.{pin.Name}";
    }
}
