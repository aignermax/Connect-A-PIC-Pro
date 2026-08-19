using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Home;
using Shouldly;

namespace UnitTests.Onboarding.FirstStepsTutorial;

/// <summary>
/// Tests for the Home screen's "Learn Lunima" entry point (issue #1080):
/// the card delegates to the <see cref="HomeViewModel.LearnTutorialRequested"/>
/// callback wired by MainViewModel, which owns the fresh-design + tour start.
/// </summary>
public class HomeLearnTutorialTests : IDisposable
{
    private readonly string _testPreferencesPath;
    private readonly string _emptyExamplesBase;
    private readonly HomeViewModel _home;

    public HomeLearnTutorialTests()
    {
        _testPreferencesPath = Path.Combine(Path.GetTempPath(), $"test-learn-prefs-{Guid.NewGuid()}.json");
        _emptyExamplesBase = Path.Combine(Path.GetTempPath(), $"test-learn-noexamples-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_emptyExamplesBase);
        _home = new HomeViewModel(
            new RecentProjectsService(new UserPreferencesService(_testPreferencesPath)),
            new UserPreferencesService(_testPreferencesPath),
            new ExampleDesignsService(_emptyExamplesBase));
    }

    public void Dispose()
    {
        if (File.Exists(_testPreferencesPath))
            File.Delete(_testPreferencesPath);
        if (Directory.Exists(_emptyExamplesBase))
            Directory.Delete(_emptyExamplesBase, recursive: true);
    }

    [Fact]
    public async Task LearnTutorialCommand_InvokesCallback()
    {
        var invoked = 0;
        _home.LearnTutorialRequested = () =>
        {
            invoked++;
            return Task.CompletedTask;
        };

        await _home.LearnTutorialCommand.ExecuteAsync(null);

        invoked.ShouldBe(1);
    }

    [Fact]
    public async Task LearnTutorialCommand_WithoutCallback_DoesNotThrow()
    {
        await _home.LearnTutorialCommand.ExecuteAsync(null);
    }
}
