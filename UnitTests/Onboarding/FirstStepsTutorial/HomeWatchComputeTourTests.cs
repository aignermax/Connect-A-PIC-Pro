using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Home;
using Shouldly;

namespace UnitTests.Onboarding.FirstStepsTutorial;

/// <summary>
/// Tests for the Home screen's "Watch it compute" entry point (issue #1143):
/// the second tour card delegates to the
/// <see cref="HomeViewModel.WatchComputeTourRequested"/> callback wired by
/// MainViewModel, which owns the Counter-example load + tour start.
/// </summary>
public class HomeWatchComputeTourTests : IDisposable
{
    private readonly string _testPreferencesPath;
    private readonly string _emptyExamplesBase;
    private readonly HomeViewModel _home;

    public HomeWatchComputeTourTests()
    {
        _testPreferencesPath = Path.Combine(Path.GetTempPath(), $"test-watch-prefs-{Guid.NewGuid()}.json");
        _emptyExamplesBase = Path.Combine(Path.GetTempPath(), $"test-watch-noexamples-{Guid.NewGuid():N}");
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
    public async Task WatchComputeTourCommand_InvokesCallback()
    {
        var invoked = 0;
        _home.WatchComputeTourRequested = () =>
        {
            invoked++;
            return Task.CompletedTask;
        };

        await _home.WatchComputeTourCommand.ExecuteAsync(null);

        invoked.ShouldBe(1);
    }

    [Fact]
    public async Task WatchComputeTourCommand_WithoutCallback_DoesNotThrow()
    {
        await _home.WatchComputeTourCommand.ExecuteAsync(null);
    }
}
