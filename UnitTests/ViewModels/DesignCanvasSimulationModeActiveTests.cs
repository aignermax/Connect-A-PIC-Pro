using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;

namespace UnitTests.ViewModels;

/// <summary>
/// Tests for <see cref="DesignCanvasViewModel.IsSimulationModeActive"/> (#690):
/// laser on/off icons must be visible during the CW overlay AND during Transient
/// mode (which clears the CW overlay), so the canvas flag is an OR of both.
/// </summary>
public class DesignCanvasSimulationModeActiveTests
{
    [Fact]
    public void IsSimulationModeActive_IsFalse_ByDefault()
    {
        new DesignCanvasViewModel().IsSimulationModeActive.ShouldBeFalse();
    }

    [Fact]
    public void IsSimulationModeActive_IsTrue_WhenCwOverlayShown()
    {
        var canvas = new DesignCanvasViewModel { ShowPowerFlow = true };

        canvas.IsSimulationModeActive.ShouldBeTrue();
    }

    [Fact]
    public void IsSimulationModeActive_IsTrue_InTransientMode_WithoutCwOverlay()
    {
        var canvas = new DesignCanvasViewModel
        {
            ShowPowerFlow = false,
            IsTransientModeActive = true
        };

        canvas.IsSimulationModeActive.ShouldBeTrue();
    }

    [Fact]
    public void IsSimulationModeActive_RaisesPropertyChanged_WhenEitherFlagChanges()
    {
        var canvas = new DesignCanvasViewModel();
        var raised = 0;
        canvas.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DesignCanvasViewModel.IsSimulationModeActive))
                raised++;
        };

        canvas.ShowPowerFlow = true;
        canvas.IsTransientModeActive = true;

        raised.ShouldBe(2);
    }
}
