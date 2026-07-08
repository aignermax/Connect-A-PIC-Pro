using System.Text.RegularExpressions;

namespace CAP_Core.Export.PythonEnvironmentManager;

/// <summary>
/// Validation gate for user-supplied environment manager inputs. Environment names
/// flow into <c>Path.Combine</c> and ultimately a recursive <c>Directory.Delete</c>,
/// so anything path-like (separators, traversal segments, rooted paths) must be
/// rejected before it reaches the filesystem.
/// </summary>
public static partial class EnvironmentNaming
{
    /// <summary>Maximum accepted length for an environment name.</summary>
    public const int MaxNameLength = 64;

    [GeneratedRegex(@"^\d{1,2}(\.\d{1,3}){0,2}$")]
    private static partial Regex PythonVersionRegex();

    /// <summary>
    /// True when <paramref name="name"/> is a plain identifier-like name:
    /// ASCII letters, digits, <c>-</c>, <c>_</c>, or <c>.</c>, not starting with a dot
    /// (which also rules out the <c>.</c>/<c>..</c> traversal segments).
    /// </summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= MaxNameLength
        && !name.StartsWith('.')
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>
    /// True when <paramref name="version"/> looks like a plain Python version
    /// (e.g. <c>3</c>, <c>3.11</c>, <c>3.11.4</c>). Rejects anything that could
    /// smuggle extra command-line content into the uv invocation.
    /// </summary>
    public static bool IsValidPythonVersion(string? version) =>
        !string.IsNullOrWhiteSpace(version) && PythonVersionRegex().IsMatch(version);

    /// <summary>
    /// True when <paramref name="path"/> resolves to a location strictly inside
    /// <paramref name="baseDir"/> (never the base directory itself). Use this as the
    /// guard before any destructive operation on a stored environment path.
    /// </summary>
    public static bool IsInsideDirectory(string baseDir, string path)
    {
        var fullBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDir))
                       + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return fullPath.StartsWith(fullBase, comparison);
    }
}
