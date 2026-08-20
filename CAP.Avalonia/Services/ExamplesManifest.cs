using System.Text.Json;
using System.Text.Json.Serialization;

namespace CAP.Avalonia.Services;

/// <summary>
/// Curated metadata for the shipped example designs, loaded from an optional
/// <c>examples.json</c> next to the .lun files: learning-path order
/// (<see cref="ExamplesManifestEntry.Rank"/>), difficulty level, and the
/// localization key of a one-line description. A file-based manifest (rather
/// than a table compiled into the service) ships together with the examples
/// folder it describes and can be curated without recompiling; a missing or
/// malformed manifest degrades to the plain alphabetical listing.
/// </summary>
internal sealed class ExamplesManifest
{
    /// <summary>File name of the manifest inside the examples directory.</summary>
    public const string FileName = "examples.json";

    /// <summary>Curated entries; empty when the manifest lists nothing.</summary>
    [JsonPropertyName("examples")]
    public List<ExamplesManifestEntry> Examples { get; set; } = new();

    /// <summary>
    /// Loads the manifest from <paramref name="examplesDirectory"/>, or returns
    /// null when the file is missing, unreadable, or malformed.
    /// </summary>
    public static ExamplesManifest? TryLoad(string examplesDirectory)
    {
        var manifestPath = Path.Combine(examplesDirectory, FileName);
        if (!File.Exists(manifestPath))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ExamplesManifest>(File.ReadAllText(manifestPath));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// One curated manifest entry: which .lun file it describes, where it sits on
/// the learning path, and how to describe it.
/// </summary>
internal sealed class ExamplesManifestEntry
{
    /// <summary>File name (including .lun) of the example inside the examples directory.</summary>
    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    /// <summary>Learning-path position; lower ranks show first.</summary>
    [JsonPropertyName("rank")]
    public int Rank { get; set; }

    /// <summary>Difficulty band of the ladder rung (Basics / Adders / Datapath).</summary>
    [JsonPropertyName("level")]
    public string? Level { get; set; }

    /// <summary>Localization key of the one-line description shown on the Home screen.</summary>
    [JsonPropertyName("descriptionKey")]
    public string? DescriptionKey { get; set; }
}
