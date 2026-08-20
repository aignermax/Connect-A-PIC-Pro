using CAP.Avalonia.Services;
using Shouldly;

namespace UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ExampleDesignsService"/> — discovery of shipped
/// example designs in an <c>examples/</c> directory found by walking up from
/// the application base directory (same strategy as the preview-script lookup).
/// </summary>
public class ExampleDesignsServiceTests : IDisposable
{
    private readonly string _rootDirectory;
    private readonly string _examplesDirectory;
    private readonly string _nestedBaseDirectory;

    public ExampleDesignsServiceTests()
    {
        _rootDirectory = Path.Combine(Path.GetTempPath(), $"example-designs-test-{Guid.NewGuid():N}");
        _examplesDirectory = Path.Combine(_rootDirectory, "examples");
        _nestedBaseDirectory = Path.Combine(_rootDirectory, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(_examplesDirectory);
        Directory.CreateDirectory(_nestedBaseDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    [Fact]
    public void EmptyExamplesDirectory_ReturnsNoExamples()
    {
        var service = new ExampleDesignsService(_nestedBaseDirectory);

        service.GetExamples().ShouldBeEmpty();
    }

    [Fact]
    public void LunFilesInExamplesDirectory_AreDiscoveredViaWalkUp()
    {
        File.WriteAllText(Path.Combine(_examplesDirectory, "mzi.lun"), "{}");

        var service = new ExampleDesignsService(_nestedBaseDirectory);

        var example = service.GetExamples().ShouldHaveSingleItem();
        example.Name.ShouldBe("mzi");
        example.FilePath.ShouldBe(Path.Combine(_examplesDirectory, "mzi.lun"));
    }

    [Fact]
    public void Examples_AreSortedByNameCaseInsensitive()
    {
        File.WriteAllText(Path.Combine(_examplesDirectory, "Zeta.lun"), "{}");
        File.WriteAllText(Path.Combine(_examplesDirectory, "alpha.lun"), "{}");

        var service = new ExampleDesignsService(_nestedBaseDirectory);

        service.GetExamples().Select(e => e.Name).ShouldBe(new[] { "alpha", "Zeta" });
    }

    [Fact]
    public void NonLunFiles_AreIgnored()
    {
        File.WriteAllText(Path.Combine(_examplesDirectory, "readme.md"), "docs");

        var service = new ExampleDesignsService(_nestedBaseDirectory);

        service.GetExamples().ShouldBeEmpty();
    }

    [Fact]
    public void Manifest_OrdersCuratedExamplesByRank_UncuratedAppendAlphabetically()
    {
        File.WriteAllText(Path.Combine(_examplesDirectory, "zebra.lun"), "{}");
        File.WriteAllText(Path.Combine(_examplesDirectory, "beta.lun"), "{}");
        File.WriteAllText(Path.Combine(_examplesDirectory, "alpha.lun"), "{}");
        WriteManifest("""
            {
              "examples": [
                { "file": "beta.lun", "rank": 2, "level": "Adders", "descriptionKey": "Examples.Beta.Description" },
                { "file": "zebra.lun", "rank": 1, "level": "Basics", "descriptionKey": "Examples.Zebra.Description" }
              ]
            }
            """);

        var examples = new ExampleDesignsService(_nestedBaseDirectory).GetExamples();

        examples.Select(e => e.Name).ShouldBe(new[] { "zebra", "beta", "alpha" });
        examples[0].DescriptionKey.ShouldBe("Examples.Zebra.Description");
        examples[0].Level.ShouldBe("Basics");
        examples[2].DescriptionKey.ShouldBeNull("uncurated files carry no manifest metadata");
        examples[2].Description.ShouldBeEmpty();
    }

    [Fact]
    public void ManifestEntryForMissingFile_IsIgnored()
    {
        File.WriteAllText(Path.Combine(_examplesDirectory, "real.lun"), "{}");
        WriteManifest("""
            { "examples": [ { "file": "ghost.lun", "rank": 1, "descriptionKey": "Examples.Ghost.Description" } ] }
            """);

        var examples = new ExampleDesignsService(_nestedBaseDirectory).GetExamples();

        examples.ShouldHaveSingleItem().Name.ShouldBe("real");
    }

    [Fact]
    public void MalformedManifest_DegradesToPlainAlphabeticalOrder()
    {
        File.WriteAllText(Path.Combine(_examplesDirectory, "zeta.lun"), "{}");
        File.WriteAllText(Path.Combine(_examplesDirectory, "alpha.lun"), "{}");
        WriteManifest("{ this is not json");

        var examples = new ExampleDesignsService(_nestedBaseDirectory).GetExamples();

        examples.Select(e => e.Name).ShouldBe(new[] { "alpha", "zeta" });
        examples.ShouldAllBe(e => e.DescriptionKey == null);
    }

    private void WriteManifest(string json) =>
        File.WriteAllText(Path.Combine(_examplesDirectory, "examples.json"), json);
}
