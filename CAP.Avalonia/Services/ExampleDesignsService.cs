using CAP.Avalonia.Services.Localization;

namespace CAP.Avalonia.Services;

/// <summary>
/// A shipped example design: display name (file name without extension) and
/// the absolute path of its .lun file. Curated examples (listed in the
/// <c>examples.json</c> manifest) additionally carry their ladder level and
/// the localization key of a one-line description.
/// </summary>
/// <param name="Name">Display name shown on the Home screen.</param>
/// <param name="FilePath">Absolute path to the example's .lun file.</param>
/// <param name="DescriptionKey">Localization key of the one-line description, or null for uncurated examples.</param>
/// <param name="Level">Difficulty band of the ladder rung (Basics / Adders / Datapath), or null for uncurated examples.</param>
public record ExampleDesign(string Name, string FilePath, string? DescriptionKey = null, string? Level = null)
{
    /// <summary>
    /// Localized one-line description shown under the name on the Home screen;
    /// empty for uncurated examples. Resolved lazily so it follows the active
    /// UI language whenever the list is rebuilt.
    /// </summary>
    public string Description =>
        DescriptionKey == null ? "" : LocalizationService.Instance.Translate(DescriptionKey);
}

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

    /// <summary>Sort rank for .lun files the manifest does not curate — they append after the curated ladder.</summary>
    private const int UncuratedRank = int.MaxValue;

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
    /// Returns the shipped example designs in learning-path order: curated
    /// entries from the <c>examples.json</c> manifest sorted by rank, then any
    /// uncurated .lun files alphabetically (a missing or malformed manifest
    /// degrades to a plain alphabetical list). Empty when no examples
    /// directory exists.
    /// </summary>
    public IReadOnlyList<ExampleDesign> GetExamples()
    {
        var examplesDirectory = FindExamplesDirectory();
        if (examplesDirectory == null)
            return Array.Empty<ExampleDesign>();

        var curatedByFile = LoadCuratedEntries(examplesDirectory);
        return Directory.GetFiles(examplesDirectory, DesignFilePattern)
            .OrderBy(path => GetRank(path, curatedByFile))
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => CreateExample(path, curatedByFile))
            .ToList();
    }

    /// <summary>Manifest entries keyed by their .lun file name (case-insensitive).</summary>
    private static IReadOnlyDictionary<string, ExamplesManifestEntry> LoadCuratedEntries(string examplesDirectory)
    {
        var manifest = ExamplesManifest.TryLoad(examplesDirectory);
        if (manifest == null)
            return new Dictionary<string, ExamplesManifestEntry>();

        return manifest.Examples
            .Where(entry => !string.IsNullOrWhiteSpace(entry.File))
            .GroupBy(entry => entry.File, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Learning-path rank of a file; uncurated files sort after all curated ones.</summary>
    private static int GetRank(string path, IReadOnlyDictionary<string, ExamplesManifestEntry> curatedByFile) =>
        curatedByFile.TryGetValue(Path.GetFileName(path), out var entry) ? entry.Rank : UncuratedRank;

    /// <summary>Creates the example for a .lun file, attaching manifest metadata when curated.</summary>
    private static ExampleDesign CreateExample(string path, IReadOnlyDictionary<string, ExamplesManifestEntry> curatedByFile)
    {
        curatedByFile.TryGetValue(Path.GetFileName(path), out var entry);
        return new ExampleDesign(Path.GetFileNameWithoutExtension(path), path, entry?.DescriptionKey, entry?.Level);
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
