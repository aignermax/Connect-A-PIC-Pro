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

    public FdtdBackendSelectionViewModelTests() =>
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);

    // Test double that is both a solver and a cost estimator, like Tidy3D.
    public abstract class Tidy3dLikeService : IFdtdSMatrixService, IFdtdCostEstimator
    {
        public abstract Task<FdtdSMatrixResult> SolveAsync(
            FdtdSMatrixRequest request, IProgress<string>? progress = null, CancellationToken ct = default);

        public abstract Task<FdtdAvailability> CheckAvailabilityAsync(CancellationToken ct = default);

        public abstract Task<FdtdCostEstimate> EstimateCostAsync(
            FdtdSMatrixRequest request, CancellationToken ct = default);
    }

    private FdtdBackendSelectionViewModel NewViewModel() => new(new FdtdBackendRegistry(
        new Dictionary<FdtdBackendType, IFdtdSMatrixService>
        {
            [FdtdBackendType.MeepDocker] = _meep.Object,
            [FdtdBackendType.Tidy3D] = _tidy3d.Object,
        },
        new UserPreferencesService(_prefsPath)));

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
        vm.CurrentSolverLabel.ShouldBe("Tidy3D Cloud");
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
               .ReturnsAsync(FdtdAvailability.Unavailable("No Tidy3D API key configured."));
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
               .ReturnsAsync(FdtdAvailability.Unavailable("No Tidy3D API key configured."));
        var vm = NewViewModel();

        vm.SelectedBackend = FdtdBackendType.Tidy3D;

        // The setter fires the probe fire-and-forget; wait for it to land.
        for (var i = 0; i < 100 && !vm.IsCurrentBackendUnavailable; i++)
            await Task.Delay(20);
        vm.IsCurrentBackendUnavailable.ShouldBeTrue();
        vm.AvailabilityHint.ShouldContain("API key");
        vm.ShowMissingKeyLink.ShouldBeTrue();
    }
}
