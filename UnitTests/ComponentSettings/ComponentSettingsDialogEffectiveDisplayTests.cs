using System.Numerics;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.ComponentSettings;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using CAP_DataAccess.Persistence.PIR;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.ComponentSettings;

/// <summary>
/// Tests for the dialog's "Currently effective S-matrix" read-only section
/// and the per-row source tag (PDK Default vs Override active). The display
/// logic answers "what does the simulator actually use right now?" so a user
/// can audit the source of the S-matrix without inferring it from the
/// override list.
/// </summary>
public class ComponentSettingsDialogEffectiveDisplayTests
{
    /// <summary>
    /// 2-port S-matrix with strong P1↔P2 cross-coupling — what a 50:50 splitter
    /// or directional coupler looks like. The preview ignores diagonals
    /// (reflections), so this builds the off-diagonal entries the preview
    /// actually surfaces.
    /// </summary>
    private static (SMatrix matrix, List<Pin> pins) BuildSimple2PortCoupling(double transmission = 0.7)
    {
        var pin1 = new Pin("port 1", 0, MatterType.Light, RectSide.Left);
        var pin2 = new Pin("port 2", 1, MatterType.Light, RectSide.Right);
        var pins = new List<Pin> { pin1, pin2 };
        var allIds = new List<Guid>
        {
            pin1.IDInFlow, pin1.IDOutFlow,
            pin2.IDInFlow, pin2.IDOutFlow
        };

        var sm = new SMatrix(allIds, new List<(Guid, double)>());
        sm.SetValues(new Dictionary<(Guid, Guid), Complex>
        {
            // S(out, in) keyed via (input.IDInFlow, output.IDOutFlow).
            // P1 → P2 transmission and P2 → P1 transmission — the values the
            // strongest-couplings preview will show.
            [(pin1.IDInFlow, pin2.IDOutFlow)] = new Complex(transmission, 0),
            [(pin2.IDInFlow, pin1.IDOutFlow)] = new Complex(transmission, 0)
        });
        return (sm, pins);
    }

    [Fact]
    public void Configure_NoEffectiveData_HidesSection()
    {
        var vm = new ComponentSettingsDialogViewModel(Mock.Of<IFileDialogService>());
        vm.Configure("comp_1", "comp_1", "MyComp", new Dictionary<string, ComponentSMatrixData>());

        vm.HasEffectiveEntries.ShouldBeFalse();
        vm.EffectiveEntries.Count.ShouldBe(0);
    }

    [Fact]
    public void Configure_EffectiveDataNoOverride_TagsAllAsPdkDefault()
    {
        // Build a 2-port SMatrix with strong cross-coupling so the preview has
        // something to show — diagonals (reflections) are intentionally skipped.
        var (sm, pins) = BuildSimple2PortCoupling(transmission: 0.7);
        var effective = new Dictionary<int, SMatrix> { [1550] = sm };

        var vm = new ComponentSettingsDialogViewModel(Mock.Of<IFileDialogService>());
        vm.Configure(
            "comp_1",
            "comp_1",
            "MyComp",
            new Dictionary<string, ComponentSMatrixData>(),
            effectiveSMatrices: effective,
            effectivePins: pins);

        vm.HasEffectiveEntries.ShouldBeTrue();
        vm.EffectiveEntries.Count.ShouldBe(1);
        vm.EffectiveEntries[0].SourceTag.ShouldBe("PDK Default");
        vm.EffectiveEntries[0].IsOverridden.ShouldBeFalse();
        vm.EffectiveEntries[0].WavelengthLabel.ShouldBe("1550 nm");
        vm.EffectiveEntries[0].Dimensions.ShouldBe("2 × 2");
        vm.EffectiveEntries[0].MagnitudePreview.ShouldContain("P1→P2=0.700");
        vm.EffectiveEntries[0].MagnitudePreview.ShouldNotContain("|S11|");
    }

    [Fact]
    public void Configure_OverrideMatchingWavelength_TagsOnlyThatRow()
    {
        // Pinning the per-row tag logic: a wavelength gets "Override active"
        // only when the active store has an entry under the same wavelength
        // key. Other wavelengths in the effective map stay PDK-driven.
        var (sm, pins) = BuildSimple2PortCoupling();
        var effective = new Dictionary<int, SMatrix> { [1550] = sm, [1310] = sm };

        var store = new Dictionary<string, ComponentSMatrixData>
        {
            ["comp_1"] = new ComponentSMatrixData
            {
                SourceNote = "imported",
                Wavelengths =
                {
                    ["1550"] = new SMatrixWavelengthEntry
                    {
                        Rows = 2, Cols = 2,
                        Real = new List<double> { 0.5, 0, 0, 0.5 },
                        Imag = new List<double> { 0, 0, 0, 0 }
                    }
                }
            }
        };

        var vm = new ComponentSettingsDialogViewModel(Mock.Of<IFileDialogService>());
        vm.Configure(
            "comp_1",
            "comp_1",
            "MyComp",
            store,
            effectiveSMatrices: effective,
            effectivePins: pins);

        var byWavelength = vm.EffectiveEntries.ToDictionary(e => e.WavelengthLabel);
        byWavelength["1550 nm"].IsOverridden.ShouldBeTrue();
        // #582: the tag carries the override's provenance (the store's SourceNote).
        byWavelength["1550 nm"].SourceTag.ShouldBe("Override active — imported");
        byWavelength["1310 nm"].IsOverridden.ShouldBeFalse();
        byWavelength["1310 nm"].SourceTag.ShouldBe("PDK Default");
    }

    [Fact]
    public void DeleteEntry_RefreshesEffectiveSourceTagToPdkDefault()
    {
        // After a user deletes their override the corresponding effective row
        // must flip back to "PDK Default". Without the refresh the user would
        // see stale "Override active" labels on a row they just unset.
        var (sm, pins) = BuildSimple2PortCoupling();
        var effective = new Dictionary<int, SMatrix> { [1550] = sm };

        var store = new Dictionary<string, ComponentSMatrixData>
        {
            ["comp_1"] = new ComponentSMatrixData
            {
                Wavelengths =
                {
                    ["1550"] = new SMatrixWavelengthEntry
                    {
                        Rows = 2, Cols = 2,
                        Real = new List<double> { 0.5, 0, 0, 0.5 },
                        Imag = new List<double> { 0, 0, 0, 0 }
                    }
                }
            }
        };

        var vm = new ComponentSettingsDialogViewModel(Mock.Of<IFileDialogService>());
        vm.Configure(
            "comp_1",
            "comp_1",
            "MyComp",
            store,
            effectiveSMatrices: effective,
            effectivePins: pins);

        vm.EffectiveEntries[0].IsOverridden.ShouldBeTrue();

        vm.DeleteEntryCommand.Execute(vm.SMatrixEntries[0]);

        vm.EffectiveEntries.Count.ShouldBe(1);
        vm.EffectiveEntries[0].IsOverridden.ShouldBeFalse();
        vm.EffectiveEntries[0].SourceTag.ShouldBe("PDK Default");
    }

    [Fact]
    public void ComponentTemplate_HasUserGlobalSMatrixOverride_IsObservable()
    {
        // The badge property must fire PropertyChanged so the ListBox
        // re-renders when the user edits an override; otherwise the user
        // would have to scroll or close/open the panel to see the badge.
        var template = new ComponentTemplate
        {
            Name = "2x2 MMI Coupler",
            PdkSource = "siepic-ebeam-pdk"
        };

        bool propertyChangedFired = false;
        template.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ComponentTemplate.HasUserGlobalSMatrixOverride))
                propertyChangedFired = true;
        };

        template.HasUserGlobalSMatrixOverride = true;

        propertyChangedFired.ShouldBeTrue();
    }
}
