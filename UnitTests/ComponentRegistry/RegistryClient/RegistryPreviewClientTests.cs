using CAP_Core.ComponentRegistry.RegistryClient;
using Shouldly;
using Xunit;

namespace UnitTests.ComponentRegistry.RegistryClient;

/// <summary>
/// Verifies <see cref="RegistryClient.GetPreviewAsync"/>: SVG previews are
/// fetched cache-first like the JSON documents, work offline once cached,
/// and never throw on missing previews or network failure (issue #771).
/// </summary>
public class RegistryPreviewClientTests : IDisposable
{
    private readonly RegistryTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static async Task<RegistryIndexEntry> GetYBranchEntry(
        CAP_Core.ComponentRegistry.RegistryClient.RegistryClient client) =>
        (await client.GetIndexAsync()).Value!.Components.Single(c => c.Id == "y-branch-1x2");

    [Fact]
    public async Task GetPreviewAsync_ReturnsSvgText_FromNetworkThenFromCache()
    {
        var client = _harness.CreateClient();
        var entry = await GetYBranchEntry(client);
        var requestsAfterIndex = _harness.Handler.RequestCount;

        var first = await client.GetPreviewAsync(entry);
        var second = await client.GetPreviewAsync(entry);

        first.IsSuccess.ShouldBeTrue();
        first.Source.ShouldBe(RegistrySource.Network);
        first.Value!.ShouldContain("<svg");
        first.Value!.ShouldContain("<polygon");
        second.Source.ShouldBe(RegistrySource.Cache);
        second.Value.ShouldBe(first.Value);
        _harness.Handler.RequestCount.ShouldBe(requestsAfterIndex + 1);
    }

    [Fact]
    public async Task GetPreviewAsync_OfflineAfterCaching_ServesSvgFromCache()
    {
        var client = _harness.CreateClient();
        var entry = await GetYBranchEntry(client);
        await client.GetPreviewAsync(entry);

        _harness.Handler.SimulateNetworkFailure = true;
        var offlineClient = _harness.CreateClient(); // Fresh client, same cache directory.

        var result = await offlineClient.GetPreviewAsync(entry);
        result.IsSuccess.ShouldBeTrue();
        result.Source.ShouldBe(RegistrySource.Cache);
        result.Value!.ShouldContain("<svg");
    }

    [Fact]
    public async Task GetPreviewAsync_EntryWithoutPreviewField_FailsWithoutNetworkRequest()
    {
        var client = _harness.CreateClient();
        var requestsBefore = _harness.Handler.RequestCount;

        var result = await client.GetPreviewAsync(new RegistryIndexEntry { Id = "legacy" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
        _harness.Handler.RequestCount.ShouldBe(requestsBefore);
    }

    [Fact]
    public async Task GetPreviewAsync_MissingDocument_ReturnsFailureWithHttpStatus_WithoutThrowing()
    {
        var client = _harness.CreateClient();

        var result = await client.GetPreviewAsync(new RegistryIndexEntry
        {
            Id = "ghost",
            Preview = "processes/generic-si220/components/ghost/geometry/preview.svg",
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("HTTP 404");
    }

    [Fact]
    public async Task GetPreviewAsync_NetworkFailureWithoutCache_ReturnsFailure_WithoutThrowing()
    {
        var client = _harness.CreateClient();
        var entry = await GetYBranchEntry(client);
        _harness.Handler.SimulateNetworkFailure = true;

        var result = await client.GetPreviewAsync(entry);

        result.IsSuccess.ShouldBeFalse();
        result.Source.ShouldBe(RegistrySource.None);
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetPreviewAsync_ForceRefresh_ReDownloadsEvenWhenCached()
    {
        var client = _harness.CreateClient();
        var entry = await GetYBranchEntry(client);
        await client.GetPreviewAsync(entry);
        var requestsAfterFirst = _harness.Handler.RequestCount;

        var refreshed = await client.GetPreviewAsync(entry, forceRefresh: true);

        refreshed.Source.ShouldBe(RegistrySource.Network);
        _harness.Handler.RequestCount.ShouldBe(requestsAfterFirst + 1);
    }
}
