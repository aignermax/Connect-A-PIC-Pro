using CAP_Core.Components.Connections;

namespace CAP_Core.Routing.RerouteImported;

/// <summary>
/// Decides which connections the "Re-route imported routes" action (issue #857) may
/// hand back to the live router. Imported GDS routes arrive as frozen cached routes
/// with the default Auto style; anything the user shaped deliberately — an explicit
/// routing style, manual bend/segment edits, a locked connection — is never
/// re-routed silently.
/// </summary>
public static class ImportedRouteRerouteEligibility
{
    /// <summary>
    /// True when <paramref name="connection"/> is a frozen, unedited optical route
    /// that a re-route pass may replace with a fresh A* route: frozen with actual
    /// geometry, Auto style (an explicit style is the user's choice and is frozen
    /// by design), not electrical (metal traces are #854), not locked, and without
    /// manual bend/segment edits (hand-edited geometry is sacred).
    /// </summary>
    public static bool IsEligible(WaveguideConnection connection) =>
        connection.IsRouteFrozen
        && !connection.IsElectrical
        && !connection.IsLocked
        && connection.Type == WaveguideType.Auto
        && !connection.HasManualPathEdits
        && connection.RoutedPath is { Segments.Count: > 0 };

    /// <summary>
    /// True when <paramref name="connection"/> is a frozen optical route that is
    /// EXCLUDED from re-routing only because it carries manual edits — surfaced in
    /// the UI so the user sees why those routes are kept unchanged.
    /// </summary>
    public static bool IsKeptHandEdited(WaveguideConnection connection) =>
        connection.IsRouteFrozen
        && !connection.IsElectrical
        && connection.Type == WaveguideType.Auto
        && connection.HasManualPathEdits;
}
