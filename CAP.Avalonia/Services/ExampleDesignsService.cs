namespace CAP.Avalonia.Services;

/// <summary>
/// A shipped example design: display name (file name without extension) and
/// the absolute path of its .lun file.
/// </summary>
/// <param name="Name">Display name shown on the Home screen.</param>
/// <param name="FilePath">Absolute path to the example's .lun file.</param>
public record ExampleDesign(string Name, string FilePath);

/// <summary>
/// Discovers shipped example designs: the nearest <c>examples/</c> directory
/// found by walking up from the application base directory (the same strategy
/// used to locate the repo's <c>scripts/</c> assets). Returns nothing when no
/// examples ship with this installation, so the Home screen hides the section.
/// </summary>
public class ExampleDesignsService
{
    /// <summary>Directory name that holds shipped example designs.</summary>
    private const string ExamplesFolderName = "examples";

    /// <summary>File pattern for Lunima design files.</summary>
    private const string DesignFilePattern = "*.lun";

    private readonly string _baseDirectory;

    /// <summary>Initializes a new instance of <see cref="ExampleDesignsService"/>.</summary>
    /// <param name="baseDirectory">
    /// Directory the walk-up starts from; defaults to the application base
    /// directory. Overridable for tests.
    /// </param>
    public ExampleDesignsService(string? baseDirectory = null)
    {
        _baseDirectory = baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
    }

    /// <summary>
    /// Returns the shipped example designs sorted by name, or an empty list
    /// when no examples directory exists.
    /// </summary>
    public IReadOnlyList<ExampleDesign> GetExamples()
    {
        var examplesDirectory = FindExamplesDirectory();
        if (examplesDirectory == null)
            return Array.Empty<ExampleDesign>();

        return Directory.GetFiles(examplesDirectory, DesignFilePattern)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => new ExampleDesign(Path.GetFileNameWithoutExtension(path), path))
            .ToList();
    }

    /// <summary>
    /// Walks up from the base directory looking for an <c>examples/</c> folder
    /// (covers both dev tree and a publish layout with examples beside the binary).
    /// </summary>
    private string? FindExamplesDirectory()
    {
        var current = new DirectoryInfo(_baseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, ExamplesFolderName);
            if (Directory.Exists(candidate))
                return candidate;
            current = current.Parent;
        }

        return null;
    }
}
