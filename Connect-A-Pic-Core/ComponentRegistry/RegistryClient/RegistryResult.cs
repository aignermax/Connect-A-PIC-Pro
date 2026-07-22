namespace CAP_Core.ComponentRegistry.RegistryClient;

/// <summary>Where a <see cref="RegistryResult{T}"/> value came from.</summary>
public enum RegistrySource
{
    /// <summary>Freshly downloaded from the registry.</summary>
    Network,

    /// <summary>Served from the local on-disk cache.</summary>
    Cache,

    /// <summary>No value available (network failed and nothing cached).</summary>
    None,
}

/// <summary>
/// Result of a registry fetch. Never throws on network failure — instead the
/// caller inspects <see cref="IsSuccess"/>, <see cref="Source"/> and
/// <see cref="ErrorMessage"/> to decide how to react (e.g. show offline state).
/// </summary>
/// <typeparam name="T">The deserialized payload type.</typeparam>
public class RegistryResult<T> where T : class
{
    private RegistryResult(T? value, RegistrySource source, string? errorMessage)
    {
        Value = value;
        Source = source;
        ErrorMessage = errorMessage;
    }

    /// <summary>The payload, or null when <see cref="IsSuccess"/> is false.</summary>
    public T? Value { get; }

    /// <summary>Where the value came from.</summary>
    public RegistrySource Source { get; }

    /// <summary>Reason for failure when <see cref="IsSuccess"/> is false, otherwise null.</summary>
    public string? ErrorMessage { get; }

    /// <summary>True when <see cref="Value"/> holds a usable payload.</summary>
    public bool IsSuccess => Value is not null;

    /// <summary>Creates a successful result with the given payload and origin.</summary>
    public static RegistryResult<T> Success(T value, RegistrySource source) =>
        new(value, source, null);

    /// <summary>Creates a failed result carrying the reason for inspection by the caller.</summary>
    public static RegistryResult<T> Failure(string errorMessage) =>
        new(null, RegistrySource.None, errorMessage);
}
