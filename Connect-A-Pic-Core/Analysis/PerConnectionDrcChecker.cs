using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;

namespace CAP_Core.Analysis;

/// <summary>
/// The per-connection DRC-lite aggregation (issue #936): resolves each connection's
/// rule set once through the caller-provided resolver, then runs the spacing and
/// min-width checks against those per-connection values instead of one design-wide
/// rule set — so every chiplet on a multi-process canvas answers to its own foundry
/// limits, including in Playground where no process lock exists at all. A connection
/// whose resolver returns null has "no PDK opinion" (built-ins, PDK-less components)
/// and falls back to the canvas-wide values, the same rule as the router's
/// per-connection bend floor (#937); a non-null but empty rule set means the endpoint
/// PDKs are known and declare no minimum — the connection stays silent (#926).
/// </summary>
public class PerConnectionDrcChecker
{
    private readonly WaveguideSpacingDetector _spacingDetector = new();
    private readonly WaveguideMinWidthChecker _minWidthChecker = new();

    /// <summary>
    /// Runs the spacing and min-width checks keyed per connection.
    /// </summary>
    /// <param name="connections">The connections to check.</param>
    /// <param name="groups">ComponentGroups whose frozen internal paths join the spacing check.</param>
    /// <param name="canvasWideMinSpacingMicrometers">
    /// Design-wide spacing fallback: governs frozen group paths (no pins to resolve)
    /// and connections with no PDK opinion.
    /// </param>
    /// <param name="canvasWideWidthRules">
    /// Design-wide min-width fallback for connections with no PDK opinion.
    /// </param>
    /// <param name="connectionDrcRuleProvider">
    /// Resolves the rule set governing one connection from its endpoint PDKs.
    /// </param>
    /// <returns>One list with every spacing and min-width finding, empty when compliant.</returns>
    public List<DesignIssue> Check(
        IReadOnlyList<WaveguideConnection> connections,
        IEnumerable<ComponentGroup> groups,
        double canvasWideMinSpacingMicrometers,
        IReadOnlyList<WaveguideMinWidthRule>? canvasWideWidthRules,
        Func<WaveguideConnection, ConnectionDrcRules?> connectionDrcRuleProvider)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(connectionDrcRuleProvider);

        var resolved = new Dictionary<WaveguideConnection, ConnectionDrcRules?>();
        foreach (var connection in connections)
        {
            resolved[connection] = connectionDrcRuleProvider(connection);
        }

        var issues = new List<DesignIssue>();
        issues.AddRange(_spacingDetector.DetectViolations(
            connections,
            groups,
            canvasWideMinSpacingMicrometers,
            connection => resolved[connection]?.MinSpacingMicrometers ?? canvasWideMinSpacingMicrometers));
        issues.AddRange(_minWidthChecker.CheckConnections(
            connections,
            connection => resolved[connection]?.WidthRules ?? canvasWideWidthRules));
        return issues;
    }
}
