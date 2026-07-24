using CAP.Avalonia.Services;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.FormulaReading;
using CAP_Core.Export;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using System.Numerics;
using Xunit;

namespace UnitTests.Components;

/// <summary>
/// Tests for SiEPIC EBeam PDK integration: loading, S-matrix creation, multi-wavelength,
/// and Nazca export with real PDK function names.
/// </summary>
public class SiepicPdkTests
{
    private static string GetSiepicPdkPath() =>
        Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", "..",
            "CAP-DataAccess", "PDKs", "siepic-ebeam-pdk.json");

    [Fact]
    public void LoadSiepicPdk_LoadsAllComponents()
    {
        var path = GetSiepicPdkPath();
        if (!File.Exists(path)) return; // skip in CI

        var loader = new PdkLoader();
        var pdk = loader.LoadFromFile(path);

        pdk.Name.ShouldBe("SiEPIC EBeam PDK");
        pdk.Foundry.ShouldBe("UBC / SiEPIC");
        pdk.DefaultWavelengthNm.ShouldBe(1550);
        // Issue #92: Expanded from 12 to 44 components
        pdk.Components.Count.ShouldBe(44);
    }

    [Fact]
    public void LoadSiepicPdk_YBranch_HasCorrectPins()
    {
        var path = GetSiepicPdkPath();
        if (!File.Exists(path)) return;

        var loader = new PdkLoader();
        var pdk = loader.LoadFromFile(path);

        var yBranch = pdk.Components.First(c => c.Name.Contains("Y-Branch"));
        yBranch.Pins.Count.ShouldBe(3);
        yBranch.Pins[0].Name.ShouldBe("port 1");
        yBranch.Pins[1].Name.ShouldBe("port 2");
        yBranch.Pins[2].Name.ShouldBe("port 3");
        yBranch.NazcaFunction.ShouldBe("ebeam_y_1550");
    }

    [Fact]
    public void LoadSiepicPdk_DcHalfringStraight_UpperRingPortsFaceUp()
    {
        // #636 introduced vertical angles for the ring-arc ports (port 2/4 at
        // offsetY≈0, the bbox TOP edge) but wrote them in the Nazca Y-up math
        // convention (90°). The app model is Y-down (see PhysicalPin /
        // NazcaCoordinateMapper: nazca angle = -app angle), so "exits upward on
        // screen" is 270° — the same convention the crossings and the Bond Pad
        // already use and exactly what Auto-Calibrate writes
        // (PdkOffsetCalibration.FlipAngleConvention). Round 5 recalibrated the
        // halfring from the real GDS: nazca pin angle 90 → app 270.
        var path = GetSiepicPdkPath();
        if (!File.Exists(path)) return;

        var loader = new PdkLoader();
        var pdk = loader.LoadFromFile(path);

        var halfring = pdk.Components.First(c => c.NazcaFunction == "ebeam_dc_halfring_straight");
        halfring.Pins.First(p => p.Name == "port 2").AngleDegrees.ShouldBe(270);
        halfring.Pins.First(p => p.Name == "port 4").AngleDegrees.ShouldBe(270);
        // Lower straight-bus ports remain horizontal.
        halfring.Pins.First(p => p.Name == "port 1").AngleDegrees.ShouldBe(180);
        halfring.Pins.First(p => p.Name == "port 3").AngleDegrees.ShouldBe(0);
        // The calibrated origin offset is the bbox TOP edge above the cell org
        // (YMax = 10.71), not -YMin (0.75) — the #635 convention bug that
        // shipped as a 9.96 µm pin offset in the round-5 field report.
        halfring.NazcaOriginOffsetY.ShouldBe(10.71);
    }

    [Fact]
    public void LoadSiepicPdk_HasMultiWavelengthData()
    {
        var path = GetSiepicPdkPath();
        if (!File.Exists(path)) return;

        var loader = new PdkLoader();
        var pdk = loader.LoadFromFile(path);

        var yBranch = pdk.Components.First(c => c.Name.Contains("Y-Branch"));
        yBranch.SMatrix.ShouldNotBeNull();
        yBranch.SMatrix.WavelengthData.ShouldNotBeNull();
        yBranch.SMatrix.WavelengthData!.Count.ShouldBeGreaterThanOrEqualTo(5);

        // Should have data around 1550nm
        var near1550 = yBranch.SMatrix.WavelengthData
            .OrderBy(w => Math.Abs(w.WavelengthNm - 1550))
            .First();
        Math.Abs(near1550.WavelengthNm - 1550).ShouldBeLessThanOrEqualTo(5);
        near1550.Connections.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void CreateSMatrixFromPdk_YBranch_HasNonZeroTransmission()
    {
        var path = GetSiepicPdkPath();
        if (!File.Exists(path)) return;

        var loader = new PdkLoader();
        var pdk = loader.LoadFromFile(path);

        var yBranch = pdk.Components.First(c => c.Name.Contains("Y-Branch"));

        // Create logical pins matching the PDK pin names
        var pins = new List<Pin>
        {
            new("port 1", 0, MatterType.Light, RectSide.Left),
            new("port 2", 1, MatterType.Light, RectSide.Right),
            new("port 3", 2, MatterType.Light, RectSide.Right)
        };

        // Create S-matrix using the same logic as MainViewModel.CreateSMatrixFromPdk
        var pinIds = pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();
        var sMatrix = new SMatrix(pinIds, new List<(Guid, double)>());

        var pinByName = pins.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
        var transfers = new Dictionary<(Guid, Guid), Complex>();

        foreach (var conn in yBranch.SMatrix!.Connections)
        {
            if (!pinByName.TryGetValue(conn.FromPin, out var fromPin) ||
                !pinByName.TryGetValue(conn.ToPin, out var toPin))
                continue;

            var phaseRad = conn.PhaseDegrees * Math.PI / 180.0;
            var value = Complex.FromPolarCoordinates(conn.Magnitude, phaseRad);
            transfers[(fromPin.IDInFlow, toPin.IDOutFlow)] = value;
            transfers[(toPin.IDInFlow, fromPin.IDOutFlow)] = value;
        }

        sMatrix.SetValues(transfers);

        // Y-Branch should have ~0.693 transmission from port 1 to ports 2 and 3
        var nonNull = sMatrix.GetNonNullValues();
        nonNull.Count.ShouldBeGreaterThan(0);

        // Check port 1 -> port 2 transmission (S21)
        var s21 = nonNull.FirstOrDefault(kv =>
            kv.Key.PinIdStart == pins[0].IDInFlow && kv.Key.PinIdEnd == pins[1].IDOutFlow);
        s21.Value.Magnitude.ShouldBeGreaterThan(0.5, "Y-branch should have ~50% transmission to each output");
        s21.Value.Magnitude.ShouldBeLessThan(0.8);

        // Check port 1 -> port 3 transmission (S31) - should be similar
        var s31 = nonNull.FirstOrDefault(kv =>
            kv.Key.PinIdStart == pins[0].IDInFlow && kv.Key.PinIdEnd == pins[2].IDOutFlow);
        s31.Value.Magnitude.ShouldBeGreaterThan(0.5);

        // Check port 1 reflection (S11) - should be small
        var s11 = nonNull.FirstOrDefault(kv =>
            kv.Key.PinIdStart == pins[0].IDInFlow && kv.Key.PinIdEnd == pins[0].IDOutFlow);
        s11.Value.Magnitude.ShouldBeLessThan(0.1, "Y-branch should have low reflection");
    }

    [Fact]
    public void NazcaExport_SiepicComponent_UsesPdkFunctionName()
    {
        // Arrange - a component with a SiEPIC function name
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());

        var component = new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: "ebeam_y_1550",
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: "Y-Branch",
            rotationCounterClock: DiscreteRotation.R0
        );

        // Act
        var result = SimpleNazcaExporter.GetNazcaFunction(component);

        // Assert - should use the stored PDK function directly, not demofab heuristic
        result.ShouldBe("ebeam_y_1550()");
    }

    [Fact]
    public void NazcaExport_SiepicComponentWithParams_IncludesParams()
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());

        var component = new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: "ebeam_dc_te1550",
            nazcaFunctionParams: "gap=200e-9",
            parts: parts,
            typeNumber: 0,
            identifier: "DC",
            rotationCounterClock: DiscreteRotation.R0
        );

        var result = SimpleNazcaExporter.GetNazcaFunction(component);
        result.ShouldBe("ebeam_dc_te1550(gap=200e-9)");
    }

    [Fact]
    public void NazcaExport_DemofabComponent_StillUsesHeuristic()
    {
        // Non-PDK function names should still use the heuristic
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>());

        var component = new Component(
            laserWaveLengthToSMatrixMap: new Dictionary<int, SMatrix>(),
            sliders: new List<Slider>(),
            nazcaFunctionName: "grating_coupler",
            nazcaFunctionParams: "",
            parts: parts,
            typeNumber: 0,
            identifier: "GC",
            rotationCounterClock: DiscreteRotation.R0
        );

        var result = SimpleNazcaExporter.GetNazcaFunction(component);
        result.ShouldBe("demo.io()");
    }

    [Fact]
    public void IsPdkFunction_EbeamPrefix_ReturnsTrue()
    {
        NazcaCoordinateMapper.IsPdkFunction("ebeam_y_1550").ShouldBeTrue();
        NazcaCoordinateMapper.IsPdkFunction("ebeam_dc_te1550").ShouldBeTrue();
        NazcaCoordinateMapper.IsPdkFunction("ebeam_gc_te1550").ShouldBeTrue();
    }

    [Fact]
    public void IsPdkFunction_DottedName_ReturnsTrue()
    {
        NazcaCoordinateMapper.IsPdkFunction("siepic_ebeam_pdk.ebeam_y_1550").ShouldBeTrue();
        NazcaCoordinateMapper.IsPdkFunction("amf.mmi2x2").ShouldBeTrue();
    }

    [Fact]
    public void IsPdkFunction_HeuristicName_ReturnsFalse()
    {
        NazcaCoordinateMapper.IsPdkFunction("grating_coupler").ShouldBeFalse();
        NazcaCoordinateMapper.IsPdkFunction("phase_shifter").ShouldBeFalse();
        NazcaCoordinateMapper.IsPdkFunction("splitter_1x2").ShouldBeFalse();
    }

    [Fact]
    public void NearestWavelengthFallback_FindsClosest()
    {
        // Create a wavelength map with non-standard keys
        var pins = new List<Pin> { new("p1", 0, MatterType.Light, RectSide.Left) };
        var pinIds = pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();

        var sm1500 = new SMatrix(pinIds, new());
        var sm1548 = new SMatrix(pinIds, new());
        var sm1600 = new SMatrix(pinIds, new());

        // Set a distinguishable value in the 1548nm matrix
        var transfers = new Dictionary<(Guid, Guid), Complex>
        {
            { (pins[0].IDInFlow, pins[0].IDOutFlow), new Complex(0.5, 0) }
        };
        sm1548.SetValues(transfers);

        var wavelengthMap = new Dictionary<int, SMatrix>
        {
            { 1500, sm1500 },
            { 1548, sm1548 },
            { 1600, sm1600 }
        };

        // Request 1550nm - should fall back to nearest (1548nm)
        var found = wavelengthMap.TryGetValue(1550, out _);
        found.ShouldBeFalse("1550 should not be an exact match");

        // Nearest-wavelength lookup (same logic as SystemMatrixBuilder)
        var nearestKey = wavelengthMap.Keys
            .OrderBy(k => Math.Abs(k - 1550))
            .First();
        nearestKey.ShouldBe(1548);

        var nearestMatrix = wavelengthMap[nearestKey];
        var values = nearestMatrix.GetNonNullValues();
        values.Count.ShouldBe(1);
        values.First().Value.Magnitude.ShouldBe(0.5);
    }

    [Fact]
    public void LoadSiepicPdk_GratingCouplerTE1550_HasCorrectPinOffset()
    {
        // Issue #66: Grating Coupler TE 1550 position offset in GDS export
        // The first pin offset should be used as NazcaOriginOffset when loading PDK components
        var path = GetSiepicPdkPath();
        if (!File.Exists(path)) return;

        var loader = new PdkLoader();
        var pdk = loader.LoadFromFile(path);

        var gratingCoupler = pdk.Components.First(c => c.Name == "Grating Coupler TE 1550");
        gratingCoupler.ShouldNotBeNull();

        // Calibrated against the actual SiEPIC ebeam_gc_te1550 cell — width
        // and height come from the cell's bbox, the single PinRec is on the
        // chip-side waveguide (no fiber-side geometry in SiEPIC's GDS).
        // Validates that the loader passes the calibration through; the
        // exact numbers are pinned by PdkJsonSaverRoundTripTests.
        gratingCoupler.WidthMicrometers.ShouldBeGreaterThan(0);
        gratingCoupler.HeightMicrometers.ShouldBeGreaterThan(0);
        gratingCoupler.Pins.Count.ShouldBe(1);
        gratingCoupler.NazcaOriginOffsetX.ShouldNotBeNull();
        gratingCoupler.NazcaOriginOffsetY.ShouldNotBeNull();

        var firstPin = gratingCoupler.Pins[0];
        firstPin.OffsetXMicrometers.ShouldBeInRange(0, gratingCoupler.WidthMicrometers);
        firstPin.OffsetYMicrometers.ShouldBeInRange(0, gratingCoupler.HeightMicrometers);
    }
}
