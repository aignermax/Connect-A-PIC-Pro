using System.Collections.Generic;
using CAP.Avalonia.ViewModels.ComponentSettings;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using Shouldly;
using Xunit;

namespace UnitTests.ComponentSettings;

/// <summary>
/// Verifies the per-wavelength source tag of the "currently effective S-matrix"
/// rows (#582): PDK-default rows and override rows must be distinguishable, and
/// an override's provenance note (e.g. "FDTD Meep 2D") must be visible.
/// </summary>
public class EffectiveSMatrixEntryViewModelTests
{
    private static (SMatrix Matrix, List<Pin> Pins) SampleMatrix()
    {
        var component = TestComponentFactory.CreateStraightWaveGuide();
        var pins = component.GetAllPins();
        return (component.WaveLengthToSMatrixMap[StandardWaveLengths.RedNM], pins);
    }

    [Fact]
    public void SourceTag_IsPdkDefault_WhenNotOverridden()
    {
        var (matrix, pins) = SampleMatrix();

        var vm = new EffectiveSMatrixEntryViewModel(1550, matrix, pins, isOverridden: false);

        vm.SourceTag.ShouldBe("PDK Default");
        vm.IsOverridden.ShouldBeFalse();
    }

    [Fact]
    public void SourceTag_IncludesSourceNote_WhenOverriddenWithProvenance()
    {
        var (matrix, pins) = SampleMatrix();

        var vm = new EffectiveSMatrixEntryViewModel(
            1550, matrix, pins, isOverridden: true, overrideSourceNote: "FDTD Meep 2D");

        vm.SourceTag.ShouldBe("Override active — FDTD Meep 2D");
        vm.IsOverridden.ShouldBeTrue();
    }

    [Fact]
    public void SourceTag_StaysGeneric_WhenOverriddenWithoutProvenance()
    {
        var (matrix, pins) = SampleMatrix();

        var vm = new EffectiveSMatrixEntryViewModel(1550, matrix, pins, isOverridden: true);

        vm.SourceTag.ShouldBe("Override active");
    }
}
