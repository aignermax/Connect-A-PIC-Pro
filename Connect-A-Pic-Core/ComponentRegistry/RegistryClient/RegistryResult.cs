namespace CAP_Core.ComponentRegistry;

/// <summary>Where a <see cref="RegistryResult{T}"/> value came from.</summary>
public enum RegistryDataSource
{
    /// <summary>Freshly downloaded from the registry.</summary>
    Network,

    /// <summary>Served from the local cache (possibly because the network failed).</summary>
    Cache,

    /// <summary>Neither network nor cache could provide the data; <c>Value</c> is null.</summary>
    Unavailable,
}

/// <summary>
/// Outcome of a registry fetch. Network failures never throw — callers inspect
/// <see cref="Source"/> and <see cref="Error"/> instead, so offline operation
/// degrades gracefully to cached data.
/// </summary>
/// <typeparam name="T">The deserialized payload type.</typeparam>
public sealed class RegistryResult<T> where T : class
{
    private RegistryResult(T? value, RegistryDataSource source, string? error)
    {
        Value = value;
        Source = source;
        Error = error;
    }

    /// <summary>Gets the payload, or null when <see cref="Source"/> is <see cref="RegistryDataSource.Unavailable"/>.</summary>
    public T? Value { get; }

    /// <summary>Gets where the payload came from.</summary>
    public RegistryDataSource Source { get; }

    /// <summary>Gets the network/parse error message, if one occurred (may be set even when cached data was returned).</summary>
    public string? Error { get; }

    /// <summary>Creates a result for a successful download.</summary>
    public static RegistryResult<T> FromNetwork(T value) => new(value, RegistryDataSource.Network, null);

    /// <summary>Creates a result served from the local cache.</summary>
    public static RegistryResult<T> FromCache(T value, string? error = null) => new(value, RegistryDataSource.Cache, error);

    /// <summary>Creates a result for data that is neither online nor cached.</summary>
    public static RegistryResult<T> Unavailable(string error) => new(null, RegistryDataSource.Unavailable, error);
}
