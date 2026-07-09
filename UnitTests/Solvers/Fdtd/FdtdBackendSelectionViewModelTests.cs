using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Solvers;
using CAP.Avalonia.ViewModels.Solvers;
using CAP_Core.Solvers.Fdtd;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Solvers.Fdtd;

/// <summary>
/// Verifies the reusable backend picker ViewModel: display names, selection
/// switching (persisted), the paid-backend flag, and availability hints.
/// </summary>
public class FdtdBackendSelectionViewModelTests : IDisposable
{
    private readonly string _prefsPath =
        Path.Combine(Path.GetTempPath(), $"prefs_{Guid.NewGuid():N}.json");

    private readonly Mock<IFdtdSMatrixService> _meep = new();
    private readonly Mock<Tidy3dLikeService> _tidy3d = new();

    /// <summary>Test double that is both a solver and a cost estimator, like Tidy3D.</summary>
    public abstract class Tidy3dLikeService : IFdtdSMatrixService, IFdtdCostEstimator
    {
        /// <inheritdoc/>
        public abstract Task<FdtdSMatrixResult> SolveAsync(
            FdtdSMatrixRequest request, IProgress<string>? progress = null, CancellationToken ct = default);

        /// <inheritdoc/>
        public abstract Task<FdtdAvailability> CheckAvailabilityAsync(CancellationToken ct = default);

        /// <inheritdoc/>
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
    public void AvailableBackendNames_ListsBothBackends()
    {
        var vm = NewViewModel();

        vm.AvailableBackendNames.ShouldBe(new[] { "Meep (local Docker)", "Tidy3D (cloud)" });
        vm.SelectedBackendName.ShouldBe("Meep (local Docker)");
        vm.CurrentBackendCostsCredits.ShouldBeFalse();
        vm.CurrentSolverLabel.ShouldBe("Meep");
    }

    [Fact]
    public void SelectingTidy3d_SwitchesServiceAndPersists()
    {
        var vm = NewViewModel();

        vm.SelectedBackendName = "Tidy3D (cloud)";

        vm.SelectedBackend.ShouldBe(FdtdBackendType.Tidy3D);
        vm.CurrentService.ShouldBeSameAs(_tidy3d.Object);
        vm.CurrentBackendCostsCredits.ShouldBeTrue();
        vm.CurrentSolverLabel.ShouldBe("Tidy3D");

        // A fresh picker (fresh registry, same prefs file) restores the choice.
        NewViewModel().SelectedBackendName.ShouldBe("Tidy3D (cloud)");
    }

    [Fact]
    public async Task CheckAvailability_WhenUnavailable_SetsHint()
    {
        _meep.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(FdtdAvailability.Unavailable("Docker is not running. Start Docker Desktop."));
        var vm = NewViewModel();

        var availability = await vm.CheckAvailabilityAsync();

        availability.IsAvailable.ShouldBeFalse();
        vm.AvailabilityHint.ShouldContain("Docker Desktop");
    }

    [Fact]
    public async Task CheckAvailability_WhenAvailable_ClearsHint()
    {
        _meep.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(FdtdAvailability.Available("ready"));
        var vm = NewViewModel();
        vm.AvailabilityHint = "stale hint";

        await vm.CheckAvailabilityAsync();

        vm.AvailabilityHint.ShouldBeEmpty();
    }

    [Fact]
    public async Task SwitchingBackend_ClearsStaleHint()
    {
        _meep.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(FdtdAvailability.Unavailable("Docker down"));
        var vm = NewViewModel();
        await vm.CheckAvailabilityAsync();
        vm.AvailabilityHint.ShouldNotBeEmpty();

        vm.SelectedBackendName = "Tidy3D (cloud)";

        vm.AvailabilityHint.ShouldBeEmpty();
    }
}
