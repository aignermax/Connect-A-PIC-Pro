using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.Services.Solvers;
using CAP_Core.Solvers.Fdtd;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Solvers.Fdtd;

public class FdtdBackendRegistryTests : IDisposable
{
    private readonly string _prefsPath =
        Path.Combine(Path.GetTempPath(), $"prefs_{Guid.NewGuid():N}.json");

    // Display names/labels come from the string tables; pin English (runner is de_DE).
    public FdtdBackendRegistryTests() =>
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);

    private UserPreferencesService NewPrefs() => new(_prefsPath);

    private static FdtdBackendRegistry NewRegistry(
        UserPreferencesService prefs,
        out IFdtdSMatrixService meep,
        out IFdtdSMatrixService tidy3d)
    {
        meep = Mock.Of<IFdtdSMatrixService>();
        tidy3d = Mock.Of<IFdtdSMatrixService>();
        return new FdtdBackendRegistry(
            new Dictionary<FdtdBackendType, IFdtdSMatrixService>
            {
                [FdtdBackendType.MeepDocker] = meep,
                [FdtdBackendType.Tidy3D] = tidy3d,
            },
            prefs);
    }

    public void Dispose()
    {
        if (File.Exists(_prefsPath)) File.Delete(_prefsPath);
    }

    [Fact]
    public void DefaultSelection_IsMeepDocker()
    {
        var registry = NewRegistry(NewPrefs(), out var meep, out _);

        registry.SelectedBackend.ShouldBe(FdtdBackendType.MeepDocker);
        registry.CurrentService.ShouldBeSameAs(meep);
    }

    [Fact]
    public void Selection_PersistsAcrossRegistryInstances()
    {
        var first = NewRegistry(NewPrefs(), out _, out _);
        first.SelectedBackend = FdtdBackendType.Tidy3D;

        var second = NewRegistry(NewPrefs(), out _, out var tidy3d);

        second.SelectedBackend.ShouldBe(FdtdBackendType.Tidy3D);
        second.CurrentService.ShouldBeSameAs(tidy3d);
    }

    [Fact]
    public void GetService_ReturnsBackendSpecificService()
    {
        var registry = NewRegistry(NewPrefs(), out var meep, out var tidy3d);

        registry.GetService(FdtdBackendType.MeepDocker).ShouldBeSameAs(meep);
        registry.GetService(FdtdBackendType.Tidy3D).ShouldBeSameAs(tidy3d);
    }

    [Fact]
    public void SavedBackendNotRegistered_FallsBackToFirst()
    {
        var prefs = NewPrefs();
        prefs.SetFdtdBackend(FdtdBackendType.Tidy3D);
        var meepOnly = new FdtdBackendRegistry(
            new Dictionary<FdtdBackendType, IFdtdSMatrixService>
            {
                [FdtdBackendType.MeepDocker] = Mock.Of<IFdtdSMatrixService>(),
            },
            prefs);

        meepOnly.SelectedBackend.ShouldBe(FdtdBackendType.MeepDocker);
    }

    [Fact]
    public void SettingUnregisteredBackend_Throws()
    {
        var prefs = NewPrefs();
        var meepOnly = new FdtdBackendRegistry(
            new Dictionary<FdtdBackendType, IFdtdSMatrixService>
            {
                [FdtdBackendType.MeepDocker] = Mock.Of<IFdtdSMatrixService>(),
            },
            prefs);

        Should.Throw<ArgumentOutOfRangeException>(
            () => meepOnly.SelectedBackend = FdtdBackendType.Tidy3D);
    }

    [Fact]
    public void EmptyRegistry_Throws()
    {
        Should.Throw<ArgumentException>(() => new FdtdBackendRegistry(
            new Dictionary<FdtdBackendType, IFdtdSMatrixService>(), NewPrefs()));
    }

    [Fact]
    public void UnparseableSavedBackend_FallsBackToMeepDocker()
    {
        var prefs = NewPrefs();
        prefs.SetFdtdBackend(FdtdBackendType.Tidy3D);
        // Corrupt the persisted value: unparseable must mean MeepDocker, not a crash.
        var json = File.ReadAllText(_prefsPath).Replace("Tidy3D", "SomethingElse");
        File.WriteAllText(_prefsPath, json);

        new UserPreferencesService(_prefsPath).GetFdtdBackend().ShouldBe(FdtdBackendType.MeepDocker);
    }

    [Theory]
    [InlineData(FdtdBackendType.MeepDocker, "Meep (local Docker)", "Meep")]
    [InlineData(FdtdBackendType.Tidy3D, "Tidy3D (cloud, FlexCredits)", "Tidy3D Cloud")]
    public void DisplayNameAndSolverLabel_ComeFromStringTables(
        FdtdBackendType backend, string displayName, string label)
    {
        FdtdBackendRegistry.DisplayName(backend).ShouldBe(displayName);
        FdtdBackendRegistry.SolverLabel(backend).ShouldBe(label);
    }
}
