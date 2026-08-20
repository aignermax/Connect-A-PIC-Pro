using System.Text.Json;
using CAP.Avalonia.Services.ComponentRegistry;
using CAP_Core.ComponentRegistry.RegistryClient;
using Shouldly;
using UnitTests.ComponentRegistry.RegistryClient;
using Xunit;

namespace UnitTests.ComponentRegistry.RegistryDownload;

/// <summary>
/// Tests for <see cref="RegistryComponentDraftMapper"/> (issue #773) against
/// the committed registry fixtures: the mapped draft carries ONLY the real
/// spectrum data — anything unusable aborts instead of fabricating a matrix.
/// </summary>
public class RegistryComponentDraftMapperTests
{
    private static ComponentManifest LoadManifest() =>
        JsonSerializer.Deserialize<ComponentManifest>(
            RegistryTestHarness.ReadFixture("component.json"))!;

    private static SParameterSpectrum LoadSpectrum() =>
        JsonSerializer.Deserialize<SParameterSpectrum>(
            RegistryTestHarness.ReadFixture("spectrum.json"))!;

    private static (ComponentManifest Manifest, ArtifactRef Artifact, SParameterSpectrum Spectrum) Fixture()
    {
        var manifest = LoadManifest();
        return (manifest, manifest.Artifacts.Simulated[0], LoadSpectrum());
    }

    [Fact]
    public void ToDraft_MapsNameCategoryAndBBox()
    {
        var (manifest, artifact, spectrum) = Fixture();

        var draft = RegistryComponentDraftMapper.ToDraft(manifest, artifact, "simulated", spectrum);

        draft.Name.ShouldBe("Y-branch splitter 1x2");
        draft.Category.ShouldBe(RegistryComponentDraftMapper.RegistryCategory);
        draft.WidthMicrometers.ShouldBeGreaterThan(0);
        draft.HeightMicrometers.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void ToDraft_LaysOutAllPortsWithinTheBBox()
    {
        var (manifest, artifact, spectrum) = Fixture();

        var draft = RegistryComponentDraftMapper.ToDraft(manifest, artifact, "simulated", spectrum);

        draft.Pins.Select(p => p.Name).ShouldBe(["o1", "o2", "o3"]);
        foreach (var pin in draft.Pins)
        {
            pin.OffsetXMicrometers.ShouldBeInRange(0, draft.WidthMicrometers);
            pin.OffsetYMicrometers.ShouldBeInRange(0, draft.HeightMicrometers);
            pin.PinKind.ShouldBe("Optical");
        }
        // First half of the ports (ceil(3/2) = 2) on the left edge, the rest on the right.
        draft.Pins[0].OffsetXMicrometers.ShouldBe(0);
        draft.Pins[0].AngleDegrees.ShouldBe(180);
        draft.Pins[1].OffsetXMicrometers.ShouldBe(0);
        draft.Pins[1].AngleDegrees.ShouldBe(180);
        draft.Pins[2].OffsetXMicrometers.ShouldBe(draft.WidthMicrometers);
        draft.Pins[2].AngleDegrees.ShouldBe(0);
    }

    [Fact]
    public void ToDraft_BuildsWavelengthDataForEverySample()
    {
        var (manifest, artifact, spectrum) = Fixture();

        var draft = RegistryComponentDraftMapper.ToDraft(manifest, artifact, "simulated", spectrum);

        draft.SMatrix.ShouldNotBeNull();
        draft.SMatrix.WavelengthNm.ShouldBe(1500);
        draft.SMatrix.WavelengthData!.Count.ShouldBe(41);
        draft.SMatrix.WavelengthData[0].WavelengthNm.ShouldBe(1500);
        draft.SMatrix.WavelengthData[^1].WavelengthNm.ShouldBe(1600);
    }

    [Fact]
    public void ToDraft_ConvertsReImToPolar_ExactlyAsHandComputed()
    {
        var (manifest, artifact, spectrum) = Fixture();
        var trace = spectrum.FindTrace("o1", "o2")!;
        double re = trace.Re[0]; // 0.676788
        double im = trace.Im[0]; // -0.139083

        var draft = RegistryComponentDraftMapper.ToDraft(manifest, artifact, "simulated", spectrum);

        var connection = draft.SMatrix!.WavelengthData![0].Connections
            .Single(c => c.FromPin == "o1" && c.ToPin == "o2");
        connection.Magnitude.ShouldBe(Math.Sqrt(re * re + im * im), 1e-12);
        connection.PhaseDegrees.ShouldBe(Math.Atan2(im, re) * 180.0 / Math.PI, 1e-12);
        // The flat Connections mirror the first wavelength sample.
        draft.SMatrix.Connections.Single(c => c.FromPin == "o1" && c.ToPin == "o2")
            .Magnitude.ShouldBe(connection.Magnitude, 1e-12);
    }

    [Fact]
    public void ToDraft_StampsFullProvenanceIntoSourceNote()
    {
        var (manifest, artifact, spectrum) = Fixture();

        var draft = RegistryComponentDraftMapper.ToDraft(manifest, artifact, "simulated", spectrum);

        var note = draft.SMatrix!.SourceNote!;
        note.ShouldContain("y-branch-1x2");
        note.ShouldContain("simulated");
        note.ShouldContain("analytic-model");
        note.ShouldContain("generate_demo_data.py");
        note.ShouldContain("2026-07-06");
        note.ShouldContain("MIT");
        draft.SMatrix.SourceTimestampUtc.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void ToDraft_EmptySpectrum_Throws_InsteadOfFabricatingData()
    {
        var (manifest, artifact, _) = Fixture();

        Should.Throw<InvalidDataException>(() =>
            RegistryComponentDraftMapper.ToDraft(manifest, artifact, "simulated", new SParameterSpectrum()));
    }

    [Fact]
    public void ToDraft_TracesWithUnknownPortsOnly_Throws()
    {
        var (manifest, artifact, _) = Fixture();
        var spectrum = new SParameterSpectrum
        {
            WavelengthUm = [1.55],
            S = [new SParameterTrace { From = "x1", To = "x2", Re = [0.5], Im = [0.0] }],
        };

        Should.Throw<InvalidDataException>(() =>
            RegistryComponentDraftMapper.ToDraft(manifest, artifact, "simulated", spectrum));
    }

    [Fact]
    public void ToDraft_SkipsZeroMagnitudeConnections()
    {
        var (manifest, artifact, _) = Fixture();
        var spectrum = new SParameterSpectrum
        {
            WavelengthUm = [1.55],
            S =
            [
                new SParameterTrace { From = "o1", To = "o2", Re = [0.0], Im = [0.0] },
                new SParameterTrace { From = "o1", To = "o3", Re = [0.5], Im = [0.0] },
            ],
        };

        var draft = RegistryComponentDraftMapper.ToDraft(manifest, artifact, "simulated", spectrum);

        var entry = draft.SMatrix!.WavelengthData!.ShouldHaveSingleItem();
        entry.Connections.ShouldHaveSingleItem().ToPin.ShouldBe("o3");
    }
}
