using System.Globalization;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Process;
using Component = CAP_Core.Components.Core.Component;

namespace CAP_Core.Analysis;

/// <summary>
/// Validates waveguide connections in a design and reports issues
/// such as invalid geometry (bend radius violations), blocked paths,
/// and overlapping waveguides including frozen group paths.
/// </summary>
public class DesignValidator
{
    private readonly WaveguideOverlapDetector _overlapDetector = new();

    /// <summary>
    /// Validates all provided waveguide connections and returns any issues found.
    /// Does not include frozen path overlap detection (use the overload with groups for that).
    /// </summary>
    /// <param name="connections">The connections to validate.</param>
    /// <returns>A list of design issues, empty if all connections are valid.</returns>
    public List<DesignIssue> Validate(IEnumerable<WaveguideConnection> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        var connectionList = connections.ToList();
        var issues = new List<DesignIssue>();

        foreach (var connection in connectionList)
        {
            CheckConnection(connection, issues);
        }

        return issues;
    }

    /// <summary>
    /// Validates waveguide connections and detects overlaps with frozen paths in ComponentGroups.
    /// </summary>
    /// <param name="connections">Regular waveguide connections to validate.</param>
    /// <param name="groups">ComponentGroups whose frozen internal paths are checked for overlap.</param>
    /// <returns>A list of all design issues found, empty if the design is valid.</returns>
    public List<DesignIssue> Validate(
        IEnumerable<WaveguideConnection> connections,
        IEnumerable<ComponentGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(groups);

        var connectionList = connections.ToList();
        var issues = Validate(connectionList);
        issues.AddRange(_overlapDetector.DetectOverlaps(connectionList, groups));
        return issues;
    }

    /// <summary>
    /// Checks a single connection for issues and adds them to the list.
    /// </summary>
    private static void CheckConnection(
        WaveguideConnection connection,
        List<DesignIssue> issues)
    {
        var (midX, midY) = CalculateMidpoint(connection);

        if (connection.RoutedPath?.IsInvalidGeometry == true)
        {
            var startName = FormatPinName(connection.StartPin);
            var endName = FormatPinName(connection.EndPin);
            issues.Add(new DesignIssue(
                DesignIssueType.InvalidGeometry,
                connection,
                midX,
                midY,
                $"Bend radius violation: {startName} to {endName}"));
        }

        if (connection.IsBlockedFallback)
        {
            var startName = FormatPinName(connection.StartPin);
            var endName = FormatPinName(connection.EndPin);
            issues.Add(new DesignIssue(
                DesignIssueType.BlockedPath,
                connection,
                midX,
                midY,
                $"Blocked path: {startName} to {endName}"));
        }

        if (connection.RoutedPath?.ViolatesProcessMinBendRadius == true)
        {
            var startName = FormatPinName(connection.StartPin);
            var endName = FormatPinName(connection.EndPin);
            issues.Add(new DesignIssue(
                DesignIssueType.BendRadiusBelowProcessMinimum,
                connection,
                midX,
                midY,
                $"Bend radius below process minimum: {startName} to {endName}"));
        }

        if (connection.RoutedPath?.PassesThroughComponent == true)
        {
            var startName = FormatPinName(connection.StartPin);
            var endName = FormatPinName(connection.EndPin);
            issues.Add(new DesignIssue(
                DesignIssueType.StyledRouteThroughComponent,
                connection,
                midX,
                midY,
                $"Styled route passes through a component: {startName} to {endName}"));
        }
    }

    /// <summary>
    /// Calculates the midpoint between a connection's start and end pins.
    /// </summary>
    private static (double x, double y) CalculateMidpoint(
        WaveguideConnection connection)
    {
        var (startX, startY) = connection.StartPin.GetAbsolutePosition();
        var (endX, endY) = connection.EndPin.GetAbsolutePosition();
        return ((startX + endX) / 2, (startY + endY) / 2);
    }

    /// <summary>
    /// Checks whether any components exceed the specified chip footprint and returns issues
    /// for any that are fully or partially outside the boundary (0,0) to
    /// (<paramref name="chipWidthMicrometers"/>, <paramref name="chipHeightMicrometers"/>).
    /// Components outside bounds are flagged with <see cref="DesignIssueType.OutOfBounds"/>;
    /// they are never moved or deleted.
    /// </summary>
    /// <param name="components">All placed components to check.</param>
    /// <param name="chipWidthMicrometers">Chip boundary width in micrometers.</param>
    /// <param name="chipHeightMicrometers">Chip boundary height in micrometers.</param>
    /// <returns>List of out-of-bounds issues, empty when all components are within bounds.</returns>
    public List<DesignIssue> ValidateComponentBounds(
        IEnumerable<Component> components,
        double chipWidthMicrometers,
        double chipHeightMicrometers)
    {
        ArgumentNullException.ThrowIfNull(components);

        var issues = new List<DesignIssue>();

        foreach (var component in components)
        {
            double right  = component.PhysicalX + component.WidthMicrometers;
            double bottom = component.PhysicalY + component.HeightMicrometers;

            bool outOfBounds = component.PhysicalX < 0
                || component.PhysicalY < 0
                || right  > chipWidthMicrometers
                || bottom > chipHeightMicrometers;

            if (!outOfBounds) continue;

            double centerX = component.PhysicalX + component.WidthMicrometers / 2;
            double centerY = component.PhysicalY + component.HeightMicrometers / 2;
            double wMm = chipWidthMicrometers  / 1000.0;
            double hMm = chipHeightMicrometers / 1000.0;
            string name = component.HumanReadableName ?? component.Identifier;

            issues.Add(new DesignIssue(
                DesignIssueType.OutOfBounds,
                connection: null,
                x: centerX,
                y: centerY,
                // Invariant culture: '5.0' is part of the test contract and the user-facing
                // unit format. Without this, de-DE / fr-FR machines render '5,0'.
                description: string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{name}' is outside chip bounds ({wMm:F1} × {hMm:F1} mm)")));
        }

        return issues;
    }

    /// <summary>
    /// Checks whether any placed components belong to a PDK that no longer matches the design's
    /// active fabrication process (issue #570 follow-up, LC-T4): after a process edit diverges a
    /// PDK from the design's locked process, placing NEW components from that PDK is blocked
    /// (see <c>SingleProcessPolicy.CheckPlacement</c>), but components already on the canvas are
    /// deliberately kept — this surfaces them for manual review instead of silently leaving a
    /// manufacturability problem invisible. Uses the same exemption rule as the placement guard
    /// (<see cref="SingleProcessPolicy.IsExempt"/>) so built-in and process-agnostic components
    /// are never flagged.
    /// </summary>
    /// <param name="components">All placed components to check.</param>
    /// <param name="pdkSourceByComponent">
    /// Each component's resolved PDK source name (or null for built-in/unresolved components,
    /// which are exempt). Resolution is a caller concern — this method only judges names.
    /// </param>
    /// <param name="processAgnosticPdkNames">PDK names exempt from process enforcement (tool libraries).</param>
    /// <param name="enabledPdkNames">
    /// PDK names currently allowed: under an active process lock the lock-derived member set;
    /// without one (Playground/no selection) all loaded PDK names — a component only gets flagged
    /// there when its PDK isn't loaded at all (e.g. trash-deleted while its instances were kept).
    /// </param>
    /// <param name="processLockActive">
    /// Whether a real (non-Playground) fabrication process is active. Only selects the issue
    /// wording: a process-mismatch message would be wrong when no process exists to mismatch.
    /// </param>
    /// <returns>One issue per conflicted component, empty when every component's PDK is exempt or enabled.</returns>
    public List<DesignIssue> ValidateComponentPdkCompatibility(
        IEnumerable<Component> components,
        IReadOnlyDictionary<Component, string?> pdkSourceByComponent,
        IReadOnlyCollection<string> processAgnosticPdkNames,
        IReadOnlyCollection<string> enabledPdkNames,
        bool processLockActive = true)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(pdkSourceByComponent);
        ArgumentNullException.ThrowIfNull(processAgnosticPdkNames);
        ArgumentNullException.ThrowIfNull(enabledPdkNames);

        var enabled = new HashSet<string>(enabledPdkNames, StringComparer.OrdinalIgnoreCase);
        var issues = new List<DesignIssue>();

        foreach (var component in components)
        {
            pdkSourceByComponent.TryGetValue(component, out var pdkSource);
            if (SingleProcessPolicy.IsExempt(pdkSource, processAgnosticPdkNames)) continue;
            if (enabled.Contains(pdkSource!)) continue;

            double centerX = component.PhysicalX + component.WidthMicrometers / 2;
            double centerY = component.PhysicalY + component.HeightMicrometers / 2;
            string name = component.HumanReadableName ?? component.Identifier;

            issues.Add(new DesignIssue(
                DesignIssueType.PdkProcessMismatch,
                connection: null,
                x: centerX,
                y: centerY,
                description: processLockActive
                    ? $"'{name}' belongs to '{pdkSource}', which no longer matches the active process."
                    : $"'{name}' belongs to '{pdkSource}', which is not loaded (the PDK may have been deleted or moved)."));
        }

        return issues;
    }

    /// <summary>
    /// Formats a pin name for display as "ComponentId.PinName".
    /// </summary>
    private static string FormatPinName(PhysicalPin pin)
    {
        return $"{pin.ParentComponent.Identifier}.{pin.Name}";
    }
}
