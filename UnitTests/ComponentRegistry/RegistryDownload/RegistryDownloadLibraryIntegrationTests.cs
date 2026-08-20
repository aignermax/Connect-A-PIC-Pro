using CAP.Avalonia.Services.ComponentRegistry;
using CAP.Avalonia.ViewModels.ComponentRegistry.RegistryBrowser;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;
using UnitTests.ComponentRegistry.RegistryClient;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.ComponentRegistry.RegistryDownload;

/// <summary>
/// Proves that a downloaded registry component actually surfaces in the
/// Component Library of the same session (issue #773): the service's
/// <c>onPdkSaved</c> callback wired to
/// <c>LeftPanelViewModel.RegisterCreatedPdk</c> reloads the saved PDK into
/// the library, provenance included.
/// </summary>
public class RegistryDownloadLibraryIntegrationTests : IDisposable
{
    private readonly RegistryTestHarness _harness = new();
    private readonly string _storeRoot = Path.Combine(
        Path.GetTempPath(), "lunima-registry-integration-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _harness.Dispose();
        if (Directory.Exists(_storeRoot))
            Directory.Delete(_storeRoot, recursive: true);
    }

    [Fact]
    public async Task DownloadedComponent_AppearsInLibrary_WithProvenance()
    {
        var leftPanel = MainViewModelTestHelper.CreateLeftPanelViewModel();
        var client = _harness.CreateClient();
        var store = new UserPdkStore(_storeRoot, new PdkJsonSaver(), new PdkLoader());
        var service = new RegistryDownloadService(client, store, leftPanel.RegisterCreatedPdk);

        var manifestResult = await client.GetComponentAsync(RegistryTestHarness.ManifestPath);
        manifestResult.IsSuccess.ShouldBeTrue();
        var manifest = manifestResult.Value!;
        var choice = RegistryArtifactSelector.Select(manifest)!;

        var result = await service.DownloadAsync(RegistryTestHarness.ManifestPath, manifest, choice);

        result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
        leftPanel.PdkManager.LoadedPdks.ShouldContain(p =>
            p.Name == "Registry generic-si220" && p.ComponentCount == 1);
        var template = leftPanel.AllTemplates.Single(t =>
            t.Name == "Y-branch splitter 1x2" && t.PdkSource == "Registry generic-si220");
        // The provenance travels on the source draft — Component Settings reads
        // exactly this (SourceDraft.SMatrix.SourceNote) for its provenance line.
        template.SourceDraft.SMatrix!.SourceNote.ShouldContain("y-branch-1x2");
        template.SourceDraft.SMatrix.SourceNote.ShouldContain("MIT");
        template.SourceDraft.SMatrix.WavelengthData!.Count.ShouldBe(41);
    }
}
