namespace CAP.Avalonia.Services;

/// <summary>
/// Parses command-line arguments for a .lun design file to open at startup
/// (e.g. <c>lunima mydesign.lun</c>). Also the entry point for OS file
/// associations, which pass the double-clicked file as an argument.
/// </summary>
public static class DesignFileArguments
{
    /// <summary>File extension of Lunima design files.</summary>
    private const string DesignFileExtension = ".lun";

    /// <summary>
    /// Returns the full path of the first argument that names an existing
    /// .lun file (extension compared case-insensitively), or null when none do.
    /// Malformed path arguments are skipped rather than throwing.
    /// </summary>
    public static string? FindDesignFile(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (!arg.EndsWith(DesignFileExtension, StringComparison.OrdinalIgnoreCase))
                continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(arg);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(fullPath))
                return fullPath;
        }

        return null;
    }
}
