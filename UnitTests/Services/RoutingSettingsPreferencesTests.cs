using CAP.Avalonia.Services;
using Shouldly;
using Xunit;

namespace UnitTests.Services;

/// <summary>
/// Tests persistence of the Routing settings page flags — adaptive crossing insertion
/// and diagonal routing — in user preferences. Both flags previously lived only in memory (<see cref="CAP.Avalonia.ViewModels.Canvas.CrossingInsertion.CrossingInsertionCanvasBinder.IsEnabled"/>
/// and <see cref="CAP.Avalonia.ViewModels.Canvas.DesignCanvasViewModel.UseDiagonalRouting"/>), so a
/// toggle enabled by the user was silently forgotten on the next app start.
/// </summary>
public class RoutingSettingsPreferencesTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(
        Path.GetTempPath(), $"routing-settings-prefs-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tempFile))
            File.Delete(_tempFile);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GetCrossingInsertionEnabled_NotConfigured_DefaultsToFalse()
    {
        var service = new UserPreferencesService(_tempFile);

        service.GetCrossingInsertionEnabled().ShouldBeFalse();
    }

    [Fact]
    public void SetCrossingInsertionEnabled_RoundTripsThroughFile()
    {
        var service = new UserPreferencesService(_tempFile);

        service.SetCrossingInsertionEnabled(true);

        new UserPreferencesService(_tempFile).GetCrossingInsertionEnabled().ShouldBeTrue();
    }

    [Fact]
    public void GetUseDiagonalRouting_NotConfigured_DefaultsToFalse()
    {
        var service = new UserPreferencesService(_tempFile);

        service.GetUseDiagonalRouting().ShouldBeFalse();
    }

    [Fact]
    public void SetUseDiagonalRouting_RoundTripsThroughFile()
    {
        var service = new UserPreferencesService(_tempFile);

        service.SetUseDiagonalRouting(true);

        new UserPreferencesService(_tempFile).GetUseDiagonalRouting().ShouldBeTrue();
    }

    [Fact]
    public void LoadingPreExistingPreferencesFile_WithoutRoutingKeys_DefaultsBothFlagsToFalse()
    {
        // Simulates a prefs.json written before this feature existed — the Routing
        // settings keys are entirely absent, not just false.
        File.WriteAllText(_tempFile, "{\"EnabledPdks\":[\"SiEPIC EBeam\"]}");

        var service = new UserPreferencesService(_tempFile);

        service.GetCrossingInsertionEnabled().ShouldBeFalse();
        service.GetUseDiagonalRouting().ShouldBeFalse();
    }
}
