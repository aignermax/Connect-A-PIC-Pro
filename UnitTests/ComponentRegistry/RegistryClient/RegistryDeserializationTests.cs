using CAP_Core.ComponentRegistry.RegistryClient;
using Shouldly;
using Xunit;

namespace UnitTests.ComponentRegistry.RegistryClient;

/// <summary>
/// Verifies that the real registry wire formats (fixtures copied from the
/// live repository) deserialize into the client models.
/// </summary>
public class RegistryDeserializationTests : IDisposable
{
    private readonly RegistryTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task GetIndexAsync_ReturnsFiveDemoComponentsOfGenericSi220()
    {
        var result = await _harness.CreateClient().GetIndexAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Source.ShouldBe(RegistrySource.Network);
        var index = result.Value!;
        index.SchemaVersion.ShouldBe(1);
        index.Processes.ShouldHaveSingleItem().Id.ShouldBe("generic-si220");
        index.Components.Count.ShouldBe(5);
        index.Components.ShouldAllBe(c => c.Process == "generic-si220");
        index.Components.ShouldAllBe(c => c.BestStatus == "demo");
        index.Components.Select(c => c.Id).ShouldContain("y-branch-1x2");
    }

    [Fact]
    public async Task GetIndexAsync_ParsesTierFlagsAndPaths()
    {
        var result = await _harness.CreateClient().GetIndexAsync();

        var yBranch = result.Value!.Components.Single(c => c.Id == "y-branch-1x2");
        yBranch.Path.ShouldBe(RegistryTestHarness.ManifestPath);
        yBranch.PortCount.ShouldBe(3);
        yBranch.Tiers.Simulated.ShouldBeTrue();
        yBranch.Tiers.Geometry.ShouldBeTrue();
        yBranch.Tiers.Measured.ShouldBeFalse();
    }

    [Fact]
    public async Task GetIndexAsync_ParsesRepoRelativePreviewPaths()
    {
        var result = await _harness.CreateClient().GetIndexAsync();

        result.Value!.Components.ShouldAllBe(c => !string.IsNullOrEmpty(c.Preview));
        result.Value!.Components.Single(c => c.Id == "y-branch-1x2").Preview
            .ShouldBe("processes/generic-si220/components/y-branch-1x2/geometry/preview.svg");
    }

    [Fact]
    public async Task GetComponentAsync_ParsesGeometryArtifactWithFormatAndPreview()
    {
        var result = await _harness.CreateClient()
            .GetComponentAsync(RegistryTestHarness.ManifestPath);

        var geometry = result.Value!.Artifacts.Geometry.ShouldHaveSingleItem();
        geometry.File.ShouldBe("geometry/cell.gds");
        geometry.Format.ShouldBe("gds");
        geometry.Preview.ShouldBe("geometry/preview.svg");
        geometry.Status.ShouldBe("demo");
        geometry.Provenance.Method.ShouldBe("generic-layout");
        geometry.Provenance.Tool.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void IndexEntry_WithoutPreviewAndGeometryFields_DeserializesTolerantly()
    {
        // Today's registry main has neither "preview" on index entries nor
        // "geometry" under artifacts — both are additive (photonic-registry#1).
        var entry = System.Text.Json.JsonSerializer.Deserialize<RegistryIndexEntry>(
            """{ "id": "legacy", "tiers": { "simulated": true } }""")!;
        var artifacts = System.Text.Json.JsonSerializer.Deserialize<ComponentArtifacts>(
            """{ "simulated": [], "measured": [] }""")!;

        entry.Preview.ShouldBeNull();
        artifacts.Geometry.ShouldNotBeNull();
        artifacts.Geometry.ShouldBeEmpty();
    }

    [Fact]
    public void ArtifactRef_WithoutFormatAndPreview_DeserializesTolerantly()
    {
        var artifact = System.Text.Json.JsonSerializer.Deserialize<ArtifactRef>(
            """{ "file": "simulated/analytic-demo.json", "status": "demo" }""")!;

        artifact.Format.ShouldBeNull();
        artifact.Preview.ShouldBeNull();
    }

    [Fact]
    public async Task GetComponentAsync_ReturnsManifestWithArtifactTiersAndProvenance()
    {
        var result = await _harness.CreateClient()
            .GetComponentAsync(RegistryTestHarness.ManifestPath);

        result.IsSuccess.ShouldBeTrue();
        var manifest = result.Value!;
        manifest.Id.ShouldBe("y-branch-1x2");
        manifest.Ports.Select(p => p.Name).ShouldBe(new[] { "o1", "o2", "o3" });
        manifest.Ports.ShouldAllBe(p => p.Kind == "optical");
        manifest.Properties.Passive.ShouldBeTrue();
        manifest.Properties.Reciprocal.ShouldBeTrue();
        manifest.License.ShouldBe("MIT");

        var artifact = manifest.Artifacts.Simulated.ShouldHaveSingleItem();
        artifact.File.ShouldBe(RegistryTestHarness.SpectrumFile);
        artifact.Status.ShouldBe("demo");
        artifact.Provenance.Method.ShouldBe("analytic-model");
        artifact.Provenance.CreatedBy.ShouldNotBeNullOrEmpty();
        artifact.Provenance.Date.ShouldNotBeNullOrEmpty();
        manifest.Artifacts.Measured.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetSpectrumAsync_ReturnsComplexSParametersUsableForPlotting()
    {
        var client = _harness.CreateClient();
        var manifest = (await client.GetComponentAsync(RegistryTestHarness.ManifestPath)).Value!;

        var result = await client.GetSpectrumAsync(
            RegistryTestHarness.ManifestPath, manifest.Artifacts.Simulated[0]);

        result.IsSuccess.ShouldBeTrue();
        var spectrum = result.Value!;
        spectrum.WavelengthUm.Count.ShouldBe(41);
        spectrum.WavelengthUm.First().ShouldBe(1.5);
        spectrum.WavelengthUm.Last().ShouldBe(1.6);

        var trace = spectrum.FindTrace("o1", "o2").ShouldNotBeNull();
        var complexSamples = trace.ToComplexArray();
        complexSamples.Length.ShouldBe(spectrum.WavelengthUm.Count);
        complexSamples[0].Real.ShouldBe(trace.Re[0]);
        complexSamples[0].Imaginary.ShouldBe(trace.Im[0]);
        // Demo Y-branch: |S21|^2 ≈ 0.5 minus excess loss — physically plausible magnitude.
        complexSamples.ShouldAllBe(s => s.Magnitude > 0.1 && s.Magnitude < 1.0);
    }

    [Fact]
    public void FindTrace_ReturnsNullForUnknownPortPair()
    {
        var spectrum = new SParameterSpectrum();
        spectrum.FindTrace("o1", "o9").ShouldBeNull();
    }

    [Fact]
    public void ToComplexArray_ThrowsOnMismatchedReAndImLengths()
    {
        var trace = new SParameterTrace { Re = { 1.0, 2.0 }, Im = { 0.5 } };
        Should.Throw<InvalidDataException>(() => trace.ToComplexArray());
    }
}
