using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.Services.Solvers;
using CAP.Avalonia.ViewModels.Solvers;
using CAP_Core.Solvers.Fdtd;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Solvers.Fdtd;

public class FdtdBackendSelectionViewModelTests : IDisposable
{
    private readonly string _prefsPath =
        Path.Combine(Path.GetTempPath(), $"prefs_{Guid.NewGuid():N}.json");

    private readonly Mock<IFdtdSMatrixService> _meep = new();
    private readonly Mock<Tidy3dLikeService> _tidy3d = new();

    public FdtdBackendSelectionViewModelTests()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
        // Default: every backend probes available, so the fire-and-forget probe fired
        // by a selection change never dereferences a null Task. Tests that need a
        // specific outcome re-setup their own mock on top of this default.
        _meep.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(FdtdAvailability.Available("ok"));
        _tidy3d.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdAvailability.Available("ok"));
    }

    // Test double that is both a solver and a cost estimator, like Tidy3D.
    public abstract class Tidy3dLikeService : IFdtdSMatrixService, IFdtdCostEstimator
    {
        public abstract Task<FdtdSMatrixResult> SolveAsync(
            FdtdSMatrixRequest request, IProgress<string>? progress = null, CancellationToken ct = default);

        public abstract Task<FdtdAvailability> CheckAvailabilityAsync(CancellationToken ct = default);

        public abstract Task<FdtdCostEstimate> EstimateCostAsync(
            FdtdSMatrixRequest request, CancellationToken ct = default);
    }

    private FdtdBackendRegistry NewRegistry() => new(
        new Dictionary<FdtdBackendType, IFdtdSMatrixService>
        {
            [FdtdBackendType.MeepDocker] = _meep.Object,
            [FdtdBackendType.Tidy3D] = _tidy3d.Object,
        },
        new UserPreferencesService(_prefsPath));

    private FdtdBackendSelectionViewModel NewViewModel() => new(NewRegistry());

    public void Dispose()
    {
        if (File.Exists(_prefsPath)) File.Delete(_prefsPath);
    }

    [Fact]
    public void Defaults_ToMeepBackend()
    {
        var vm = NewViewModel();

        vm.AvailableBackends.ShouldBe(new[] { FdtdBackendType.MeepDocker, FdtdBackendType.Tidy3D });
        vm.SelectedBackend.ShouldBe(FdtdBackendType.MeepDocker);
        vm.CurrentBackendCostsCredits.ShouldBeFalse();
        vm.CurrentSolverLabel.ShouldBe("Meep");
        vm.BackendItems[0].IsSelected.ShouldBeTrue();
        vm.BackendItems[1].IsSelected.ShouldBeFalse();
    }

    [Fact]
    public void SelectingTidy3d_SwitchesServiceAndPersists()
    {
        var vm = NewViewModel();

        vm.SelectedBackend = FdtdBackendType.Tidy3D;

        vm.CurrentService.ShouldBeSameAs(_tidy3d.Object);
        vm.CurrentBackendCostsCredits.ShouldBeTrue();
        vm.CurrentSolverLabel.ShouldBe("Tidy3D");
        vm.BackendItems[1].IsSelected.ShouldBeTrue();
        vm.BackendItems[0].IsSelected.ShouldBeFalse();

        // A fresh picker (fresh registry, same prefs file) restores the choice.
        NewViewModel().SelectedBackend.ShouldBe(FdtdBackendType.Tidy3D);
    }

    [Fact]
    public void ItemSelectCommand_SelectsBackend()
    {
        var vm = NewViewModel();

        vm.BackendItems[1].SelectCommand.Execute(null);

        vm.SelectedBackend.ShouldBe(FdtdBackendType.Tidy3D);
    }

    [Fact]
    public void PickerInSecondWindow_FollowsBackendChangeMadeElsewhere()
    {
        // Two windows (e.g. component settings + NewComponent editor) share the singleton registry.
        var registry = NewRegistry();
        var vmA = new FdtdBackendSelectionViewModel(registry);
        var vmB = new FdtdBackendSelectionViewModel(registry);

        vmA.SelectedBackend = FdtdBackendType.Tidy3D;

        vmB.SelectedBackend.ShouldBe(FdtdBackendType.Tidy3D);
        vmB.BackendItems[1].IsSelected.ShouldBeTrue();
        vmB.BackendItems[0].IsSelected.ShouldBeFalse();
        vmB.CurrentSolverLabel.ShouldBe("Tidy3D");
    }

    [Fact]
    public void ChangeFromOnePicker_RaisesTheRegistryEventExactlyOnce()
    {
        // The synced picker must not echo the same value back into the registry —
        // one user change, one event.
        var registry = NewRegistry();
        var vmA = new FdtdBackendSelectionViewModel(registry);
        var vmB = new FdtdBackendSelectionViewModel(registry);
        var eventCount = 0;
        registry.SelectedBackendChanged += (_, _) => eventCount++;

        vmA.SelectedBackend = FdtdBackendType.Tidy3D;

        eventCount.ShouldBe(1);
    }

    [Fact]
    public void DisposedPicker_NoLongerFollowsRegistryChanges()
    {
        var registry = NewRegistry();
        var vmA = new FdtdBackendSelectionViewModel(registry);
        var vmB = new FdtdBackendSelectionViewModel(registry);
        vmB.Dispose();

        vmA.SelectedBackend = FdtdBackendType.Tidy3D;

        vmB.SelectedBackend.ShouldBe(FdtdBackendType.MeepDocker);
    }

    [Fact]
    public async Task CheckAvailability_WhenUnavailable_SetsHintAndUnavailableFlag()
    {
        _meep.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(FdtdAvailability.Unavailable("Docker is not running. Start Docker Desktop."));
        var vm = NewViewModel();

        var availability = await vm.CheckAvailabilityAsync();

        availability.IsAvailable.ShouldBeFalse();
        vm.AvailabilityHint.ShouldContain("Docker Desktop");
        vm.HasAvailabilityHint.ShouldBeTrue();
        vm.IsCurrentBackendUnavailable.ShouldBeTrue();
    }

    [Fact]
    public async Task CheckAvailability_WhenAvailable_ClearsHintAndFlag()
    {
        _meep.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(FdtdAvailability.Available("ready"));
        var vm = NewViewModel();
        vm.AvailabilityHint = "stale hint";

        await vm.CheckAvailabilityAsync();

        vm.AvailabilityHint.ShouldBeEmpty();
        vm.IsCurrentBackendUnavailable.ShouldBeFalse();
    }

    [Fact]
    public async Task SwitchingBackend_ClearsStaleHintAndFlag()
    {
        _meep.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(FdtdAvailability.Unavailable("Docker down"));
        var vm = NewViewModel();
        await vm.CheckAvailabilityAsync();
        vm.AvailabilityHint.ShouldNotBeEmpty();

        vm.SelectedBackend = FdtdBackendType.Tidy3D;

        vm.AvailabilityHint.ShouldBeEmpty();
        vm.IsCurrentBackendUnavailable.ShouldBeFalse();
    }

    [Fact]
    public async Task MissingKeyHint_ShowsGetKeyLink_OnlyForPaidBackend()
    {
        _tidy3d.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdAvailability.Unavailable(
                   "No Tidy3D API key configured.", FdtdUnavailableReason.MissingApiKey));
        _meep.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(FdtdAvailability.Unavailable("Docker down"));
        var vm = NewViewModel();

        vm.SelectedBackend = FdtdBackendType.Tidy3D;
        await vm.CheckAvailabilityAsync();
        vm.ShowMissingKeyLink.ShouldBeTrue();

        vm.SelectedBackend = FdtdBackendType.MeepDocker;
        await vm.CheckAvailabilityAsync();
        vm.HasAvailabilityHint.ShouldBeTrue();
        vm.ShowMissingKeyLink.ShouldBeFalse();
    }

    [Fact]
    public async Task SelectingBackend_ProbesImmediately_WithoutComputeClick()
    {
        _tidy3d.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdAvailability.Unavailable(
                   "No Tidy3D API key configured.", FdtdUnavailableReason.MissingApiKey));
        var vm = NewViewModel();

        vm.SelectedBackend = FdtdBackendType.Tidy3D;

        // The setter fires the probe fire-and-forget; wait for it to land.
        for (var i = 0; i < 100 && !vm.IsCurrentBackendUnavailable; i++)
            await Task.Delay(20);
        vm.IsCurrentBackendUnavailable.ShouldBeTrue();
        vm.AvailabilityHint.ShouldContain("API key");
        vm.ShowMissingKeyLink.ShouldBeTrue();
    }

    [Fact]
    public async Task OpenTidy3dSettings_InvokesTheInjectedRoute_AndGatesTheLinkVisibility()
    {
        _tidy3d.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdAvailability.Unavailable(
                   "No Tidy3D API key configured.", FdtdUnavailableReason.MissingApiKey));
        var vm = NewViewModel();
        vm.SelectedBackend = FdtdBackendType.Tidy3D;
        await vm.CheckAvailabilityAsync();
        vm.ShowOpenSettingsLink.ShouldBeFalse("no settings route injected");

        var opened = 0;
        vm.OpenTidy3dSettingsPage = () => opened++;
        vm.ShowOpenSettingsLink.ShouldBeTrue();

        vm.OpenTidy3dSettingsCommand.Execute(null);
        opened.ShouldBe(1);

        // Without a delegate the command is a harmless no-op (test/headless wiring).
        var unwired = NewViewModel();
        Should.NotThrow(() => unwired.OpenTidy3dSettingsCommand.Execute(null));
    }

    [Fact]
    public async Task SlowProbeFromPreviousBackend_DoesNotOverwriteTheNewBackendsState()
    {
        // A slow Meep probe (Docker inspect) finishing AFTER the user switched to
        // Tidy3D must not overwrite Tidy3D's hint/flag with its stale verdict.
        var meepGate = new TaskCompletionSource<FdtdAvailability>();
        _meep.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
             .Returns(meepGate.Task);
        _tidy3d.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdAvailability.Unavailable(
                   "No Tidy3D API key configured.", FdtdUnavailableReason.MissingApiKey));
        var vm = NewViewModel();
        var staleProbe = vm.CheckAvailabilityAsync(); // Meep probe, blocked

        vm.SelectedBackend = FdtdBackendType.Tidy3D;
        // The selection change fires its own (fast) probe — wait for it to land.
        for (var i = 0; i < 100 && !vm.IsCurrentBackendUnavailable; i++)
            await Task.Delay(20);
        vm.IsCurrentBackendUnavailable.ShouldBeTrue();

        meepGate.SetResult(FdtdAvailability.Available("ok"));
        await staleProbe;

        vm.SelectedBackend.ShouldBe(FdtdBackendType.Tidy3D);
        vm.AvailabilityHint.ShouldContain("API key");
        vm.IsCurrentBackendUnavailable.ShouldBeTrue();
    }

    [Fact]
    public async Task ProbeFault_IsSurfacedAsUnavailable_InsteadOfFaultingTheProbeTask()
    {
        // A crashed probe (subprocess died, Docker socket gone) must not fault the
        // fire-and-forget selection-change task unobserved — it is an "unavailable".
        _meep.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
             .ThrowsAsync(new InvalidOperationException("docker socket gone"));
        var vm = NewViewModel();

        var availability = await vm.CheckAvailabilityAsync();

        availability.IsAvailable.ShouldBeFalse();
        vm.AvailabilityHint.ShouldContain("docker socket gone");
        vm.IsCurrentBackendUnavailable.ShouldBeTrue();
    }

    [Fact]
    public async Task UnavailabilityWithoutMissingKeyReason_HidesTheGetKeyLink_EvenForPaidBackend()
    {
        // The link only makes sense when the probe actually reported a missing key —
        // any other Tidy3D unavailability (package missing, server down) needs its own fix.
        _tidy3d.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdAvailability.Unavailable("tidy3d python package is not installed"));
        var vm = NewViewModel();
        vm.SelectedBackend = FdtdBackendType.Tidy3D;

        await vm.CheckAvailabilityAsync();

        vm.HasAvailabilityHint.ShouldBeTrue();
        vm.CurrentBackendCostsCredits.ShouldBeTrue();
        vm.ShowMissingKeyLink.ShouldBeFalse();
        vm.ShowOpenSettingsLink.ShouldBeFalse();
    }
}
