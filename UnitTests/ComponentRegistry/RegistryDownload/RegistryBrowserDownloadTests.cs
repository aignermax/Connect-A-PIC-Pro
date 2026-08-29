using System.Text.Json;
using CAP.Avalonia.Services.ComponentRegistry;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.ComponentRegistry.RegistryBrowser;
using CAP_Core.ComponentRegistry.RegistryClient;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;
using UnitTests.ComponentRegistry.RegistryClient;
using Xunit;

namespace UnitTests.ComponentRegistry.RegistryDownload;

/// <summary>
/// End-to-end tests of the registry "download to library" flow (issue #773)
/// against a stubbed registry client (fixture-fed, no network) and a
/// temp-rooted <see cref="UserPdkStore"/>.
/// </summary>
public class RegistryBrowserDownloadTests : IDisposable
{
    private readonly RegistryTestHarness _harness = new();
    private readonly string _storeRoot = Path.Combine(
        Path.GetTempPath(), "lunima-registry-download-tests", Guid.NewGuid().ToString("N"));
    private string? _registeredPath;

    public RegistryBrowserDownloadTests() =>
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);

    public void Dispose()
    {
        _harness.Dispose();
        if (Directory.Exists(_storeRoot))
            Directory.Delete(_storeRoot, recursive: true);
    }

    private UserPdkStore CreateStore() => new(_storeRoot, new PdkJsonSaver(), new PdkLoader());

    private RegistryBrowserViewModel CreateViewModel(
        CAP_Core.ComponentRegistry.RegistryClient.RegistryClient? client = null)
    {
        client ??= _harness.CreateClient();
        return new RegistryBrowserViewModel(client,
            new RegistryDownloadService(client, CreateStore(), p => _registeredPath = p));
    }

    private async Task LoadAndSelectAsync(
        RegistryBrowserViewModel vm, string manifestJson)
    {
        _harness.Handler.AddResponse(
            $"{RegistryTestHarness.BaseUrl}/{RegistryTestHarness.ManifestPath}", manifestJson);
        await vm.LoadCommand.ExecuteAsync(null);
        vm.SelectedComponent = vm.Components.Single(c => c.Id == "y-branch-1x2");
        await vm.DetailsLoadTask;
    }

    private Task LoadAndSelectAsync(RegistryBrowserViewModel vm) =>
        LoadAndSelectAsync(vm, RegistryTestHarness.ReadFixture("component.json"));

    private static string ManifestWithArtifacts(string simulatedStatus) =>
        RegistryTestHarness.ReadFixture("component.json")
            .Replace("\"status\": \"demo\"", $"\"status\": \"{simulatedStatus}\"", StringComparison.Ordinal);

    [Fact]
    public async Task Download_AdaptsFixtureComponent_IntoRegistryPdk()
    {
        var vm = CreateViewModel();
        await LoadAndSelectAsync(vm);

        vm.DownloadCommand.CanExecute(null).ShouldBeTrue();
        vm.DownloadUnavailableReason.ShouldBeNull();

        vm.DownloadCommand.Execute(null);
        await vm.DownloadTask;

        // The PDK file round-trips through the same loader every save/edit flow uses.
        _registeredPath.ShouldNotBeNull();
        File.Exists(_registeredPath).ShouldBeTrue();
        _registeredPath.ShouldStartWith(_storeRoot);
        var pdk = new PdkLoader().LoadFromFileForEditing(_registeredPath);
        pdk.Name.ShouldBe("Registry generic-si220");
        pdk.Process!.Name.ShouldBe("generic-si220");
        var component = pdk.Components.ShouldHaveSingleItem();
        component.Name.ShouldBe("Y-branch splitter 1x2");
        component.SMatrix!.WavelengthData!.Count.ShouldBe(41);
        component.SMatrix.SourceNote.ShouldNotBeNullOrEmpty(); // Provenance, shown in Component Settings.
        vm.DownloadIsError.ShouldBeFalse();
        vm.DownloadMessage.ShouldContain("Y-branch splitter 1x2");
        vm.DownloadMessage.ShouldContain("Registry generic-si220");
        vm.PendingDisputedConfirm.ShouldBeFalse();
        vm.IsDownloading.ShouldBeFalse();
    }

    [Fact]
    public async Task Download_DifferentActiveProcess_IsDisabledWithReason()
    {
        var vm = CreateViewModel();
        await LoadAndSelectAsync(vm);

        vm.ActiveProcessId = "other-process";

        vm.DownloadCommand.CanExecute(null).ShouldBeFalse();
        vm.DownloadUnavailableReason.ShouldBe(
            "This component belongs to a different process than the loaded design — it cannot be adopted.");
        vm.ActiveProcessId = "GENERIC-SI220"; // Case-insensitive match re-enables.
        vm.DownloadCommand.CanExecute(null).ShouldBeTrue();
        vm.DownloadUnavailableReason.ShouldBeNull();
    }

    [Fact]
    public async Task Download_WithdrawnOnlyArtifact_IsDisabledWithReason()
    {
        var vm = CreateViewModel();
        await LoadAndSelectAsync(vm, ManifestWithArtifacts("withdrawn"));

        vm.DownloadCommand.CanExecute(null).ShouldBeFalse();
        vm.DownloadUnavailableReason.ShouldBe(
            "No usable data published (withdrawn or missing) — nothing to download.");
        Directory.Exists(_storeRoot).ShouldBeFalse();
    }

    [Fact]
    public async Task Download_DisputedArtifact_RequiresExplicitConfirm_BeforeWritingAnything()
    {
        var vm = CreateViewModel();
        await LoadAndSelectAsync(vm, ManifestWithArtifacts("disputed"));

        vm.DownloadCommand.CanExecute(null).ShouldBeTrue();
        vm.DownloadCommand.Execute(null);
        await vm.DownloadTask;

        // First click only surfaces the warning — nothing is written.
        vm.PendingDisputedConfirm.ShouldBeTrue();
        Directory.Exists(_storeRoot).ShouldBeFalse();

        vm.ConfirmDisputedDownloadCommand.Execute(null);
        await vm.DownloadTask;

        vm.PendingDisputedConfirm.ShouldBeFalse();
        File.Exists(_registeredPath!).ShouldBeTrue();
        vm.DownloadIsError.ShouldBeFalse();
        vm.DownloadMessage.ShouldContain("Registry generic-si220");
    }

    [Fact]
    public async Task Download_NetworkFailureWithoutCache_ReportsError_AndWritesNothing()
    {
        var vm = CreateViewModel();
        await LoadAndSelectAsync(vm);
        _harness.Handler.SimulateNetworkFailure = true;

        vm.DownloadCommand.Execute(null);
        await vm.DownloadTask;

        vm.DownloadIsError.ShouldBeTrue();
        vm.DownloadMessage.ShouldStartWith("Download failed:");
        File.Exists(Path.Combine(_storeRoot, "registry-generic-si220.json")).ShouldBeFalse();
        vm.PendingDisputedConfirm.ShouldBeFalse();
    }

    [Fact]
    public async Task Download_WithoutService_KeepsBrowserReadOnly()
    {
        var vm = new RegistryBrowserViewModel(_harness.CreateClient());
        await LoadAndSelectAsync(vm);

        vm.DownloadCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Download_FinishingAfterSelectionChanged_DropsTheStaleMessage()
    {
        var vm = CreateViewModel();
        await LoadAndSelectAsync(vm);
        var spectrumUrl = $"{RegistryTestHarness.BaseUrl}/" +
            CAP_Core.ComponentRegistry.RegistryClient.RegistryClient.ResolveArtifactPath(
                RegistryTestHarness.ManifestPath, RegistryTestHarness.SpectrumFile);
        _harness.Handler.Hold(spectrumUrl);

        vm.DownloadCommand.Execute(null);
        var inFlightDownload = vm.DownloadTask;
        vm.SelectedComponent = vm.Components.First(c => c.Id != "y-branch-1x2");
        await vm.DetailsLoadTask;

        _harness.Handler.Release(spectrumUrl);
        await inFlightDownload;

        // A success message for the previously shown component would mislead.
        vm.DownloadMessage.ShouldBeNull();
        vm.DownloadIsError.ShouldBeFalse();
        vm.IsDownloading.ShouldBeFalse();
    }

    [Fact]
    public async Task SelectingAnotherComponent_ResetsMessageAndDisputedWarning()
    {
        var vm = CreateViewModel();
        await LoadAndSelectAsync(vm, ManifestWithArtifacts("disputed"));
        vm.DownloadCommand.Execute(null);
        await vm.DownloadTask;
        vm.PendingDisputedConfirm.ShouldBeTrue();

        vm.SelectedComponent = vm.Components.First(c => c.Id != "y-branch-1x2");
        await vm.DetailsLoadTask;

        vm.PendingDisputedConfirm.ShouldBeFalse();
        vm.DownloadMessage.ShouldBeNull();
    }
}
