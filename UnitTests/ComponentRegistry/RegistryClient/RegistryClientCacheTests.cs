using CAP_Core.ComponentRegistry.RegistryClient;
using Shouldly;
using Xunit;

namespace UnitTests.ComponentRegistry.RegistryClient;

/// <summary>
/// Verifies cache-first behavior, explicit refresh, offline operation and
/// malformed-JSON handling of the <see cref="RegistryClient"/>.
/// </summary>
public class RegistryClientCacheTests : IDisposable
{
    private readonly RegistryTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task SecondCall_IsServedFromCache_WithoutNetworkRequest()
    {
        var client = _harness.CreateClient();

        var first = await client.GetIndexAsync();
        var second = await client.GetIndexAsync();

        first.Source.ShouldBe(RegistrySource.Network);
        second.Source.ShouldBe(RegistrySource.Cache);
        second.Value!.Components.Count.ShouldBe(5);
        _harness.Handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task ForceRefresh_ReDownloadsEvenWhenCached()
    {
        var client = _harness.CreateClient();
        await client.GetIndexAsync();

        var refreshed = await client.GetIndexAsync(forceRefresh: true);

        refreshed.Source.ShouldBe(RegistrySource.Network);
        _harness.Handler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task OfflineAfterCaching_ServesIndexManifestAndSpectrumFromCache()
    {
        var client = _harness.CreateClient();
        var manifest = (await client.GetComponentAsync(RegistryTestHarness.ManifestPath)).Value!;
        await client.GetIndexAsync();
        await client.GetSpectrumAsync(RegistryTestHarness.ManifestPath, manifest.Artifacts.Simulated[0]);

        _harness.Handler.SimulateNetworkFailure = true;
        var offlineClient = _harness.CreateClient(); // Fresh client, same cache directory.

        (await offlineClient.GetIndexAsync()).Source.ShouldBe(RegistrySource.Cache);
        (await offlineClient.GetComponentAsync(RegistryTestHarness.ManifestPath))
            .Source.ShouldBe(RegistrySource.Cache);
        var spectrum = await offlineClient.GetSpectrumAsync(
            RegistryTestHarness.ManifestPath, manifest.Artifacts.Simulated[0]);
        spectrum.Source.ShouldBe(RegistrySource.Cache);
        spectrum.Value!.WavelengthUm.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task RefreshWhileOffline_FallsBackToCachedCopy_WithoutThrowing()
    {
        var client = _harness.CreateClient();
        await client.GetIndexAsync();
        _harness.Handler.SimulateNetworkFailure = true;

        var result = await client.GetIndexAsync(forceRefresh: true);

        result.IsSuccess.ShouldBeTrue();
        result.Source.ShouldBe(RegistrySource.Cache);
    }

    [Fact]
    public async Task OfflineWithoutCache_ReturnsFailureWithInspectableReason()
    {
        _harness.Handler.SimulateNetworkFailure = true;
        var client = _harness.CreateClient();

        var result = await client.GetIndexAsync();

        result.IsSuccess.ShouldBeFalse();
        result.Value.ShouldBeNull();
        result.Source.ShouldBe(RegistrySource.None);
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task MalformedJsonFromNetwork_ReturnsFailure_AndIsNotCached()
    {
        _harness.Handler.AddResponse($"{RegistryTestHarness.BaseUrl}/index.json", "{ not json ]");
        var client = _harness.CreateClient();

        var result = await client.GetIndexAsync();

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Malformed JSON");
        _harness.CreateCache().TryRead("index.json").ShouldBeNull();
    }

    [Fact]
    public async Task CorruptCacheEntry_FallsThroughToFreshDownload()
    {
        _harness.CreateCache().Write("index.json", "{ corrupt");
        var client = _harness.CreateClient();

        var result = await client.GetIndexAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Source.ShouldBe(RegistrySource.Network);
        _harness.Handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task MissingDocument_ReturnsFailureWithHttpStatus()
    {
        var client = _harness.CreateClient();

        var result = await client.GetComponentAsync("processes/does/not/exist.json");

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("HTTP 404");
    }

    [Fact]
    public void ResolveArtifactPath_JoinsComponentDirectoryAndArtifactFile()
    {
        CAP_Core.ComponentRegistry.RegistryClient.RegistryClient
            .ResolveArtifactPath(RegistryTestHarness.ManifestPath, "simulated/analytic-demo.json")
            .ShouldBe("processes/generic-si220/components/y-branch-1x2/simulated/analytic-demo.json");
    }

    [Fact]
    public void RegistryCache_RejectsPathTraversal()
    {
        var cache = _harness.CreateCache();
        cache.Write("../outside.json", "{}");
        cache.TryRead("../outside.json").ShouldBeNull();
    }
}
