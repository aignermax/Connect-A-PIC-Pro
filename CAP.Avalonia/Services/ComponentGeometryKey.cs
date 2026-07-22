using System;
using System.Security.Cryptography;
using System.Text;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.Services;

/// <summary>
/// Geometry identity of a component, used to scope S-matrix overrides: components with
/// the same geometry must share the same (recomputed) S-matrix. The Nazca call
/// (module|function|parameters) defines the geometry. Same identity ⇒ same key.
/// </summary>
public static class ComponentGeometryKey
{
    /// <summary>Bump to invalidate all geometry-scoped override keys.</summary>
    public const int FormatVersion = 1;

    /// <summary>ASCII unit separator — never appears in module/function/parameter strings.</summary>
    private const char FieldSeparator = (char)31;

    /// <summary>Builds the geometry key from the component's Nazca call tuple.</summary>
    public static string For(Component component)
    {
        // Separate fields so distinct tuples can't collide via boundary shifts.
        var material = $"{component.NazcaModuleName}{FieldSeparator}{component.NazcaFunctionName}{FieldSeparator}{component.NazcaFunctionParameters}";
        return $"geo:v{FormatVersion}-{Hash(material)}";
    }

    private static string Hash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes, 0, 12).ToLowerInvariant();
    }
}
