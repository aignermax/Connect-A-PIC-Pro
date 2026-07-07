using CAP_Core.ComponentRegistry;
using Shouldly;
using Xunit;

namespace UnitTests.ComponentRegistry;

/// <summary>Tests for <see cref="RegistryClient"/>: parsing, caching, and offline behavior.</summary>
public sealed class RegistryClientTests : IDisposable
{
    private const string BaseUrl = "https://fake.test/registry/";

    private readonly string _cacheDirectory = Path.Combine(
        Path.GetTempPath(), "lunima-registry-tests", Guid.NewGuid().ToString("N"));

    private readonly FakeRegistryHttpHandler _handler = new();

    public void Dispose()
    {
        if (Directory.Exists(_cacheDirectory))
            Directory.Delete(_cacheDirectory, recursive: true);
    }

    private RegistryClient CreateClient() => new(
        new HttpClient(_handler), BaseUrl, new RegistryCache(_cacheDirectory));

    private FakeRegistryHttpHandler WithAllDocuments() => _handler
        .WithDocument(RegistryClient.IndexPath, RegistryTestData.IndexJson)
        .WithDocument(RegistryTestData.YBranchPath, RegistryTestData.YBranchJson)
        .WithDocument(RegistryTestData.YBranchSpectrumPath, RegistryTestData.YBranchSpectrumJson);

    [Fact]
    public async Task GetIndexAsync_ReturnsAllFiveDemoComponents()
    {
        WithAllDocuments();

        var result = await CreateClient().GetIndexAsync();

        result.Source.ShouldBe(RegistryDataSource.Network);
        result.Value.ShouldNotBeNull();
        result.Value!.Components.Count.ShouldBe(5);
        result.Value.Components.ShouldAllBe(c => c.Process == "generic-si220");
        result.Value.Components.ShouldContain(c => c.Id == "y-branch-1x2" && c.PortCount == 3);
        result.Value.Components.ShouldAllBe(c => c.Tiers.Simulated && !c.Tiers.Measured && !c.Tiers.Geometry);
        result.Value.Processes.ShouldHaveSingleItem().Status.ShouldBe("demo");
    }

    [Fact]
    public async Task GetComponentAsync_ParsesManifestWithArtifactsAndProvenance()
    {
        WithAllDocuments();

        var result = await CreateClient().GetComponentAsync(RegistryTestData.YBranchPath);

        var component = result.Value.ShouldNotBeNull();
        component.Id.ShouldBe("y-branch-1x2");
        component.Ports.Select(p => p.Name).ShouldBe(new[] { "o1", "o2", "o3" });
        component.Properties.Passive.ShouldBeTrue();
        component.Properties.Reciprocal.ShouldBeTrue();
        var artifact = component.Artifacts.Simulated.ShouldHaveSingleItem();
        artifact.Status.ShouldBe("demo");
        artifact.File.ShouldBe("simulated/analytic-demo.json");
        artifact.Provenance.Method.ShouldBe("analytic-model");
        artifact.Provenance.Date.ShouldBe("2026-07-06");
        component.Artifacts.Measured.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetSpectrumAsync_ReturnsComplexSpectraAlignedWithWavelengths()
    {
        WithAllDocuments();

        var result = await CreateClient().GetSpectrumAsync(
            RegistryTestData.YBranchPath, "simulated/analytic-demo.json");

        var spectrum = result.Value.ShouldNotBeNull();
        spectrum.WavelengthUm.ShouldBe(new[] { 1.5, 1.55, 1.6 });
        var s12 = spectrum.GetSpectrum("o1", "o2").ShouldNotBeNull();
        s12.Length.ShouldBe(3);
        s12[1].Real.ShouldBe(-0.3, 1e-12);
        s12[1].Imaginary.ShouldBe(0.4, 1e-12);
        spectrum.GetSpectrum("o2", "o3").ShouldBeNull();
    }

    [Fact]
    public async Task SecondCall_IsServedFromCache_WithoutNetworkRequest()
    {
        WithAllDocuments();
        var client = CreateClient();

        (await client.GetIndexAsync()).Source.ShouldBe(RegistryDataSource.Network);
        var second = await client.GetIndexAsync();

        second.Source.ShouldBe(RegistryDataSource.Cache);
        second.Value!.Components.Count.ShouldBe(5);
        _handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task ForceRefresh_BypassesCacheAndRedownloads()
    {
        WithAllDocuments();
        var client = CreateClient();
        await client.GetIndexAsync();

        var refreshed = await client.GetIndexAsync(forceRefresh: true);

        refreshed.Source.ShouldBe(RegistryDataSource.Network);
        _handler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task OfflineWithWarmCache_ServesCachedDataWithoutThrowing()
    {
        WithAllDocuments();
        var client = CreateClient();
        await client.GetIndexAsync();
        _handler.Offline = true;

        var offline = await client.GetIndexAsync(forceRefresh: true);

        offline.Source.ShouldBe(RegistryDataSource.Cache);
        offline.Value!.Components.Count.ShouldBe(5);
        offline.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task OfflineWithColdCache_ReturnsUnavailableWithoutThrowing()
    {
        _handler.Offline = true;

        var result = await CreateClient().GetIndexAsync();

        result.Source.ShouldBe(RegistryDataSource.Unavailable);
        result.Value.ShouldBeNull();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task MalformedJson_ReturnsUnavailableWithoutThrowing()
    {
        _handler.WithDocument(RegistryClient.IndexPath, "{ this is not json ]");

        var result = await CreateClient().GetIndexAsync();

        result.Source.ShouldBe(RegistryDataSource.Unavailable);
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task MissingDocument_ReturnsUnavailableWithoutThrowing()
    {
        var result = await CreateClient().GetComponentAsync("processes/nope/component.json");

        result.Source.ShouldBe(RegistryDataSource.Unavailable);
        result.Error.ShouldNotBeNull();
    }
}
