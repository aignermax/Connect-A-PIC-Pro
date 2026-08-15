using System.Text.Json;
using CAP.Avalonia.Services.GdsImport.LayerVisibility;
using CAP.Avalonia.ViewModels;
using Shouldly;
using Xunit;

namespace UnitTests.GdsImport.LayerVisibility;

/// <summary>
/// The .lun round trip of the per-layer visibility section (issue #858):
/// captured entries survive JSON serialization, and files saved before the
/// feature load with all layers visible.
/// </summary>
public class GdsLayerVisibilityPersistenceTests
{
    [Fact]
    public void DesignFileData_RoundTripsLayerVisibilityEntries()
    {
        var state = new GdsLayerVisibilityState();
        state.Set(11, 0, isVisible: false, opacity: 1.0);
        state.Set(1, 0, isVisible: true, opacity: 0.4);
        var designData = new DesignFileData { LayerVisibility = state.CaptureForSave() };

        var json = JsonSerializer.Serialize(designData);
        var loaded = JsonSerializer.Deserialize<DesignFileData>(json)!;

        var restored = new GdsLayerVisibilityState();
        restored.Restore(loaded.LayerVisibility);
        restored.EffectiveOpacity(11, 0).ShouldBe(0.0);
        restored.EffectiveOpacity(1, 0).ShouldBe(0.4);
    }

    [Fact]
    public void LegacyFile_WithoutLayerVisibilitySection_LoadsAllVisible()
    {
        var loaded = JsonSerializer.Deserialize<DesignFileData>("{}")!;

        loaded.LayerVisibility.ShouldBeNull();

        var state = new GdsLayerVisibilityState();
        state.Set(11, 0, isVisible: false, opacity: 1.0);
        state.Restore(loaded.LayerVisibility);
        state.EffectiveOpacity(11, 0).ShouldBe(1.0);
    }
}
