namespace CAP_DataAccess.Persistence.PIR;

/// <summary>
/// Well-known values for <see cref="NazcaCodeOverride.Backend"/> (issue #637).
/// Stored as a string (not an enum) so .lun files remain forward/backward
/// compatible: older app versions simply ignore the property, and a null or
/// unknown value falls back to the Nazca default.
/// </summary>
public static class OverrideBackend
{
    /// <summary>The default backend — Nazca raw-code overrides (issues #556/#559).</summary>
    public const string Nazca = "nazca";

    /// <summary>gdsfactory raw-code overrides (issue #637, builds on the #581 exporter).</summary>
    public const string GdsFactory = "gdsfactory";

    /// <summary>
    /// True when <paramref name="backend"/> selects the gdsfactory backend.
    /// Null or any unrecognized value means Nazca (the pre-#637 default).
    /// </summary>
    public static bool IsGdsFactory(string? backend) =>
        string.Equals(backend, GdsFactory, StringComparison.OrdinalIgnoreCase);
}
