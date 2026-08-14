using CAP.Avalonia.Services.GdsImport.LayerVisibility;
using Shouldly;
using Xunit;

namespace UnitTests.GdsImport.LayerVisibility;

/// <summary>
/// The per-layer display state (issue #858): effective opacity per (layer, datatype)
/// pair, minimal save capture (non-default entries only), and load restore.
/// </summary>
public class GdsLayerVisibilityStateTests
{
    [Fact]
    public void EffectiveOpacity_WithoutOverride_IsFullyOpaque()
    {
        var state = new GdsLayerVisibilityState();

        state.EffectiveOpacity(11, 0).ShouldBe(1.0);
    }

    [Fact]
    public void EffectiveOpacity_HiddenLayer_IsZero_RegardlessOfStoredOpacity()
    {
        var state = new GdsLayerVisibilityState();
        state.Set(11, 0, isVisible: false, opacity: 0.8);

        state.EffectiveOpacity(11, 0).ShouldBe(0.0);
        state.EffectiveOpacity(11, 1).ShouldBe(1.0, "other datatypes are unaffected");
    }

    [Fact]
    public void EffectiveOpacity_FadedLayer_ReturnsStoredOpacity()
    {
        var state = new GdsLayerVisibilityState();
        state.Set(11, 0, isVisible: true, opacity: 0.3);

        state.EffectiveOpacity(11, 0).ShouldBe(0.3);
    }

    [Fact]
    public void Set_ClampsOpacity_IntoUnitRange()
    {
        var state = new GdsLayerVisibilityState();
        state.Set(1, 0, isVisible: false, opacity: -2.0);
        state.Set(2, 0, isVisible: true, opacity: 5.0);

        state.Get(1, 0)!.Opacity.ShouldBe(0.0);
        state.Get(2, 0).ShouldBeNull("clamped to 1.0 while visible is the default → no entry");
    }

    [Fact]
    public void Set_BackToDefault_RemovesOverride()
    {
        var state = new GdsLayerVisibilityState();
        state.Set(11, 0, isVisible: false, opacity: 1.0);
        state.Set(11, 0, isVisible: true, opacity: 1.0);

        state.Get(11, 0).ShouldBeNull();
        state.CaptureForSave().ShouldBeNull();
    }

    [Fact]
    public void CaptureForSave_ReturnsNonDefaultEntries_OrderedByLayerThenDatatype()
    {
        var state = new GdsLayerVisibilityState();
        state.Set(11, 1, isVisible: false, opacity: 1.0);
        state.Set(1, 0, isVisible: true, opacity: 0.5);
        state.Set(11, 0, isVisible: false, opacity: 1.0);

        var saved = state.CaptureForSave();

        saved.ShouldNotBeNull();
        saved.Select(e => (e.Layer, e.DataType))
            .ShouldBe(new[] { (1, 0), (11, 0), (11, 1) });
    }

    [Fact]
    public void Restore_RoundTripsCapturedEntries()
    {
        var source = new GdsLayerVisibilityState();
        source.Set(11, 0, isVisible: false, opacity: 1.0);
        source.Set(1, 0, isVisible: true, opacity: 0.25);

        var target = new GdsLayerVisibilityState();
        target.Restore(source.CaptureForSave());

        target.EffectiveOpacity(11, 0).ShouldBe(0.0);
        target.EffectiveOpacity(1, 0).ShouldBe(0.25);
    }

    [Fact]
    public void Restore_Null_ResetsToAllVisible()
    {
        var state = new GdsLayerVisibilityState();
        state.Set(11, 0, isVisible: false, opacity: 1.0);

        state.Restore(null);

        state.EffectiveOpacity(11, 0).ShouldBe(1.0);
        state.CaptureForSave().ShouldBeNull();
    }

    [Fact]
    public void Restore_SkipsDefaultEntries_AndClampsOpacity()
    {
        var state = new GdsLayerVisibilityState();
        state.Restore(new[]
        {
            new GdsLayerVisibilityData { Layer = 1, DataType = 0, IsVisible = true, Opacity = 1.0 },
            new GdsLayerVisibilityData { Layer = 2, DataType = 0, IsVisible = true, Opacity = 7.0 },
            new GdsLayerVisibilityData { Layer = 3, DataType = 0, IsVisible = true, Opacity = -1.0 },
        });

        state.Get(1, 0).ShouldBeNull("fully visible entry is the default");
        state.Get(2, 0).ShouldBeNull("opacity clamps to 1.0 → default");
        state.EffectiveOpacity(3, 0).ShouldBe(0.0, "opacity clamps to 0.0");
    }

    [Fact]
    public void Changed_IsRaisedOnEdits_ButNotOnNoOps()
    {
        var state = new GdsLayerVisibilityState();
        int raised = 0;
        state.Changed += () => raised++;

        state.Set(1, 0, isVisible: true, opacity: 1.0);
        raised.ShouldBe(0, "setting a layer that is already default is a no-op");
        state.Clear();
        raised.ShouldBe(0, "clearing an empty state is a no-op");

        state.Set(1, 0, isVisible: false, opacity: 1.0);
        raised.ShouldBe(1);
        state.Clear();
        raised.ShouldBe(2);
        state.Restore(null);
        raised.ShouldBe(3);
    }
}
