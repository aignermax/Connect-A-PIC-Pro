using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Home;
using Shouldly;

namespace UnitTests.ViewModels;

/// <summary>
/// Unit tests for the Examples surface of <see cref="HomeViewModel"/>:
/// discovery into the Examples list, HasExamples, delegation of
/// OpenExampleCommand, and the click-time re-check for vanished files.
/// </summary>
public class HomeExamplesTests : IDisposable
{
    private readonly string _testPreferencesPath;
    private readonly string _examplesRoot;
    private readonly string _exampleFilePath;
    private readonly UserPreferencesService _preferences;
    private readonly RecentProjectsService _recentProjects;

    public HomeExamplesTests()
    {
        _testPreferencesPath = Path.Combine(Path.GetTempPath(), $"test-homeexamples-prefs-{Guid.NewGuid()}.json");
        _examplesRoot = Path.Combine(Path.GetTempPath(), $"test-homeexamples-{Guid.NewGuid():N}");
        var examplesDir = Path.Combine(_examplesRoot, "examples");
        Directory.CreateDirectory(examplesDir);
        _exampleFilePath = Path.Combine(examplesDir, "demo-circuit.lun");
        File.WriteAllText(_exampleFilePath, "{}");
        _preferences = new UserPreferencesService(_testPreferencesPath);
        _recentProjects = new RecentProjectsService(_preferences);
    }

    public void Dispose()
    {
        if (File.Exists(_testPreferencesPath))
        {
            File.Delete(_testPreferencesPath);
        }
        if (Directory.Exists(_examplesRoot))
        {
            Directory.Delete(_examplesRoot, recursive: true);
        }
    }

    private HomeViewModel CreateHomeViewModel() =>
        new(_recentProjects, _preferences, new ExampleDesignsService(_examplesRoot));

    [Fact]
    public void Constructor_DiscoversShippedExamples()
    {
        var home = CreateHomeViewModel();

        home.HasExamples.ShouldBeTrue();
        var example = home.Examples.ShouldHaveSingleItem();
        example.Name.ShouldBe("demo-circuit");
        example.FilePath.ShouldBe(_exampleFilePath);
    }

    [Fact]
    public async Task OpenExampleCommand_InvokesDelegateWithExamplePath()
    {
        var home = CreateHomeViewModel();
        string? requestedPath = null;
        home.OpenExampleRequested = path =>
        {
            requestedPath = path;
            return Task.FromResult(true);
        };

        await home.OpenExampleCommand.ExecuteAsync(home.Examples[0]);

        requestedPath.ShouldBe(_exampleFilePath);
    }

    [Fact]
    public async Task OpenExample_FileDeletedAfterDiscovery_DoesNotInvokeDelegate()
    {
        var home = CreateHomeViewModel();
        var example = home.Examples[0];
        File.Delete(_exampleFilePath);

        var invoked = false;
        home.OpenExampleRequested = _ =>
        {
            invoked = true;
            return Task.FromResult(true);
        };

        await home.OpenExampleCommand.ExecuteAsync(example);

        invoked.ShouldBeFalse();
        home.HasExamples.ShouldBeFalse("the vanished example must drop from the list");
    }

    [Fact]
    public void NoExamplesInstalled_SectionHidden()
    {
        var emptyRoot = Path.Combine(Path.GetTempPath(), $"test-noexamples-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyRoot);
        try
        {
            var home = new HomeViewModel(_recentProjects, _preferences, new ExampleDesignsService(emptyRoot));

            home.HasExamples.ShouldBeFalse();
            home.Examples.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(emptyRoot, recursive: true);
        }
    }

    [Fact]
    public void CuratedExample_ExposesResolvedDescription_UncuratedHasNone()
    {
        var examplesDir = Path.Combine(_examplesRoot, "examples");
        File.WriteAllText(Path.Combine(examplesDir, "curated.lun"), "{}");
        File.WriteAllText(Path.Combine(examplesDir, "examples.json"), """
            { "examples": [ { "file": "curated.lun", "rank": 1, "level": "Basics", "descriptionKey": "Examples.NotGate.Description" } ] }
            """);

        var home = CreateHomeViewModel();

        var curated = home.Examples.Single(e => e.Name == "curated");
        curated.DescriptionKey.ShouldBe("Examples.NotGate.Description");
        curated.Level.ShouldBe("Basics");
        curated.Description.ShouldNotBeNullOrEmpty();
        curated.Description.ShouldNotBe("Examples.NotGate.Description",
            "the description must be resolved through the string tables, not leak the raw key");

        var uncurated = home.Examples.Single(e => e.Name == "demo-circuit");
        uncurated.DescriptionKey.ShouldBeNull();
        uncurated.Description.ShouldBeEmpty();

        home.Examples.First().Name.ShouldBe("curated", "the curated ladder entry sorts before uncurated files");
    }
}
