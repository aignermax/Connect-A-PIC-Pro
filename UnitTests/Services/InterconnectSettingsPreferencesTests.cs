using CAP.Avalonia.Services;
using CAP_Core.Routing.InterconnectRouting;
using Shouldly;
using Xunit;

namespace UnitTests.Services;

/// <summary>
/// Tests persistence of global interconnect settings in user preferences (issue #574).
/// </summary>
public class InterconnectSettingsPreferencesTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(
        Path.GetTempPath(), $"interconnect-prefs-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tempFile))
            File.Delete(_tempFile);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GetInterconnectSettings_NotConfigured_ReturnsExportDefaults()
    {
        var service = new UserPreferencesService(_tempFile);

        var settings = service.GetInterconnectSettings();

        settings.WidthMicrometers.ShouldBe(InterconnectSettings.DefaultWidthMicrometers);
        settings.BendRadiusMicrometers.ShouldBe(InterconnectSettings.DefaultBendRadiusMicrometers);
        settings.GdsLayer.ShouldBeNull();
    }

    [Fact]
    public void SetInterconnectSettings_RoundTripsThroughFile()
    {
        var service = new UserPreferencesService(_tempFile);

        service.SetInterconnectSettings(new InterconnectSettings
        {
            WidthMicrometers = 0.8,
            BendRadiusMicrometers = 30,
            GdsLayer = 5,
        });

        var reloaded = new UserPreferencesService(_tempFile).GetInterconnectSettings();
        reloaded.WidthMicrometers.ShouldBe(0.8);
        reloaded.BendRadiusMicrometers.ShouldBe(30);
        reloaded.GdsLayer.ShouldBe(5);
    }
}
