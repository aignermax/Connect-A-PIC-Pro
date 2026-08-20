using System.Text.Json;
using CAP.Avalonia.Services.Localization;
using Shouldly;

namespace UnitTests.Services.Localization;

/// <summary>
/// Guards the curated examples manifest (<c>examples/examples.json</c>): every
/// description key must exist in all five shipped languages — otherwise a raw
/// key leaks onto the Home screen — and every curated entry must point at a
/// .lun file that actually ships, since a typo would silently drop the example
/// from the learning path.
/// </summary>
public class ExamplesManifestLocalizationTests
{
    private sealed record ManifestEntry(string File, int Rank, string? DescriptionKey);

    [Fact]
    public void EveryManifestDescriptionKey_ExistsInEveryShippedLanguage()
    {
        var keys = ReadManifestEntries()
            .Select(entry => entry.DescriptionKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToList();

        keys.ShouldNotBeEmpty("every rung of the curated ladder must carry a description key");

        foreach (var language in SupportedLanguage.All)
        {
            var table = LocalizationResourceLoader.Load(language.Code);
            foreach (var key in keys)
            {
                table.ContainsKey(key!).ShouldBeTrue($"description key '{key}' missing in strings-{language.Code}.json");
            }
        }
    }

    [Fact]
    public void EveryManifestEntry_PointsAtAShippedLunFile_WithUniqueRank()
    {
        var examplesDirectory = Path.Combine(FindRepoRoot(), "examples");
        var entries = ReadManifestEntries();

        foreach (var entry in entries)
        {
            System.IO.File.Exists(Path.Combine(examplesDirectory, entry.File))
                .ShouldBeTrue($"manifest file '{entry.File}' does not exist in examples/");
        }

        entries.Select(entry => entry.Rank).Distinct().Count()
            .ShouldBe(entries.Count, "manifest ranks must be unique to keep the ladder order deterministic");
    }

    private static List<ManifestEntry> ReadManifestEntries()
    {
        var manifestPath = Path.Combine(FindRepoRoot(), "examples", "examples.json");
        System.IO.File.Exists(manifestPath)
            .ShouldBeTrue("examples/examples.json must ship with the examples folder");

        using var document = JsonDocument.Parse(System.IO.File.ReadAllText(manifestPath));
        return document.RootElement.GetProperty("examples").EnumerateArray()
            .Select(entry => new ManifestEntry(
                entry.GetProperty("file").GetString()!,
                entry.GetProperty("rank").GetInt32(),
                entry.TryGetProperty("descriptionKey", out var key) ? key.GetString() : null))
            .ToList();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || System.IO.File.Exists(gitPath))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root.");
    }
}
