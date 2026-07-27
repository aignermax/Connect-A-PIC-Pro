using CAP.Avalonia;
using CAP.Avalonia.Services;
using Microsoft.Extensions.DependencyInjection;

namespace UnitTests.Helpers;

/// <summary>
/// Builds the real production DI container (<see cref="App.ConfigureServices"/>) for tests
/// that need to verify composition-root wiring — e.g. that a preference restores correctly
/// into the singleton the app actually resolves at startup, not just into a hand-built
/// instance. <see cref="UserPreferencesService"/> is redirected to a throwaway file so the
/// test never touches the real user profile.
/// </summary>
public static class ProductionContainerTestHelper
{
    /// <summary>
    /// Builds the production container, pointed at <paramref name="preferencesPath"/> instead
    /// of the real user profile (which would otherwise create directories or rename the real
    /// preferences file). Last registration wins for single-service resolution, so this
    /// override replaces the one <see cref="App.ConfigureServices"/> registers.
    /// </summary>
    public static ServiceProvider BuildWithTempPreferences(string preferencesPath)
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        services.AddSingleton(new UserPreferencesService(preferencesPath));
        return services.BuildServiceProvider();
    }
}
