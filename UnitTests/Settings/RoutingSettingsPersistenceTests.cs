using CAP.Avalonia;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace UnitTests.Settings;

/// <summary>
/// Builds the real production DI container (<see cref="App.ConfigureServices"/>) to verify
/// that the Routing settings page's two flags — adaptive crossing insertion (Issue #553) and
/// diagonal routing — survive an app restart. Both were previously held only in memory, so a
/// toggle enabled by the user reverted to off the next time the app launched.
/// </summary>
public class RoutingSettingsPersistenceTests
{
    private static string NewTempPreferencesPath() =>
        Path.Combine(Path.GetTempPath(), $"lunima-routing-prefs-{Guid.NewGuid():N}.json");

    private static ServiceProvider BuildContainer(string preferencesPath)
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        // Last registration wins for single-service resolution (see AppDiContainerTests) —
        // this points the whole graph at a throwaway file instead of the real user profile.
        services.AddSingleton(new UserPreferencesService(preferencesPath));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void CrossingInsertionEnabled_PersistedTrue_BinderStartsEnabled()
    {
        var preferencesPath = NewTempPreferencesPath();
        try
        {
            new UserPreferencesService(preferencesPath).SetCrossingInsertionEnabled(true);

            using var sp = BuildContainer(preferencesPath);
            var binder = sp.GetRequiredService<CrossingInsertionCanvasBinder>();

            binder.IsEnabled.ShouldBeTrue();
        }
        finally
        {
            if (File.Exists(preferencesPath)) File.Delete(preferencesPath);
        }
    }

    [Fact]
    public void CrossingInsertionEnabled_ToggledOn_PersistsToPreferences()
    {
        var preferencesPath = NewTempPreferencesPath();
        try
        {
            using var sp = BuildContainer(preferencesPath);
            var binder = sp.GetRequiredService<CrossingInsertionCanvasBinder>();
            binder.IsEnabled.ShouldBeFalse("crossing insertion is opt-in");

            binder.IsEnabled = true;

            new UserPreferencesService(preferencesPath).GetCrossingInsertionEnabled().ShouldBeTrue();
        }
        finally
        {
            if (File.Exists(preferencesPath)) File.Delete(preferencesPath);
        }
    }

    [Fact]
    public void UseDiagonalRouting_PersistedTrue_CanvasStartsEnabled()
    {
        var preferencesPath = NewTempPreferencesPath();
        try
        {
            new UserPreferencesService(preferencesPath).SetUseDiagonalRouting(true);

            using var sp = BuildContainer(preferencesPath);
            var canvas = sp.GetRequiredService<DesignCanvasViewModel>();

            canvas.UseDiagonalRouting.ShouldBeTrue();
            canvas.Router.UseDiagonalRouting.ShouldBeTrue();
        }
        finally
        {
            if (File.Exists(preferencesPath)) File.Delete(preferencesPath);
        }
    }

    [Fact]
    public void UseDiagonalRouting_ToggledOn_PersistsToPreferences()
    {
        var preferencesPath = NewTempPreferencesPath();
        try
        {
            using var sp = BuildContainer(preferencesPath);
            var canvas = sp.GetRequiredService<DesignCanvasViewModel>();
            canvas.UseDiagonalRouting.ShouldBeFalse("diagonal routing is opt-in");

            canvas.UseDiagonalRouting = true;

            new UserPreferencesService(preferencesPath).GetUseDiagonalRouting().ShouldBeTrue();
        }
        finally
        {
            if (File.Exists(preferencesPath)) File.Delete(preferencesPath);
        }
    }
}
