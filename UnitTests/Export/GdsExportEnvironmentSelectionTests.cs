using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Export;
using CAP_Core.Export;
using Shouldly;

namespace UnitTests.Export;

/// <summary>
/// Tests for the managed-environment candidate list and the "install Nazca"
/// offer on <see cref="GdsExportViewModel"/> (settings page integration).
/// </summary>
public class GdsExportEnvironmentSelectionTests
{
    private static GdsExportViewModel CreateViewModel() =>
        new(new GdsExportService());

    [Fact]
    public void RefreshManagedCandidates_WithProvider_ListsAllCandidates()
    {
        var vm = CreateViewModel();
        vm.ManagedEnvironmentsProvider = () => new[]
        {
            new ManagedEnvCandidate("nazca", "/envs/nazca/bin/python", "Managed · nazca"),
            new ManagedEnvCandidate("py312", "/envs/py312/bin/python", "Managed · py312"),
        };

        vm.RefreshManagedCandidates();

        vm.ManagedCandidates.Count.ShouldBe(2);
        vm.ManagedCandidates[0].Name.ShouldBe("nazca");
    }

    [Fact]
    public void RefreshManagedCandidates_NoNazcaAnywhere_ShowsInstallOffer()
    {
        var vm = CreateViewModel();
        vm.NazcaAvailable = false;
        vm.ManagedEnvironmentsProvider = () => Array.Empty<ManagedEnvCandidate>();

        vm.RefreshManagedCandidates();

        vm.ShowNazcaInstallOffer.ShouldBeTrue();
    }

    [Fact]
    public void RefreshManagedCandidates_NazcaInActiveInterpreter_HidesInstallOffer()
    {
        var vm = CreateViewModel();
        vm.NazcaAvailable = true;
        vm.ManagedEnvironmentsProvider = () => Array.Empty<ManagedEnvCandidate>();

        vm.RefreshManagedCandidates();

        vm.ShowNazcaInstallOffer.ShouldBeFalse();
    }

    [Fact]
    public void RefreshManagedCandidates_ManagedEnvExists_HidesInstallOfferEvenWithoutActiveNazca()
    {
        var vm = CreateViewModel();
        vm.NazcaAvailable = false;
        vm.ManagedEnvironmentsProvider = () => new[]
        {
            new ManagedEnvCandidate("nazca", "/envs/nazca/bin/python", "Managed · nazca"),
        };

        vm.RefreshManagedCandidates();

        vm.ShowNazcaInstallOffer.ShouldBeFalse();
    }

    [Fact]
    public void RefreshManagedCandidates_WithoutProvider_IsEmptyAndOffersInstallWhenNazcaMissing()
    {
        var vm = CreateViewModel();
        vm.NazcaAvailable = false;

        vm.RefreshManagedCandidates();

        vm.ManagedCandidates.ShouldBeEmpty();
        vm.ShowNazcaInstallOffer.ShouldBeTrue();
    }

    [Fact]
    public async Task SelectManagedEnvironment_InvokesActivationDelegate()
    {
        var vm = CreateViewModel();
        string? activated = null;
        vm.ActivateManagedEnvironment = name => activated = name;
        var candidate = new ManagedEnvCandidate("nazca", "/envs/nazca/bin/python", "Managed · nazca");

        await vm.SelectManagedEnvironmentCommand.ExecuteAsync(candidate);

        activated.ShouldBe("nazca");
    }

    [Fact]
    public void InstallNazca_InvokesRequestDelegate()
    {
        var vm = CreateViewModel();
        var requested = false;
        vm.RequestNazcaInstall = () => requested = true;

        vm.InstallNazcaCommand.Execute(null);

        requested.ShouldBeTrue();
    }

    private sealed class FakeMessageBoxService : IMessageBoxService
    {
        public int ChoiceToReturn { get; set; } = -1;
        public string? LastMessage { get; private set; }
        public IReadOnlyList<string>? LastButtons { get; private set; }

        public Task<SavePromptResult> ShowSavePromptAsync(string message, string title) =>
            Task.FromResult(SavePromptResult.Cancel);

        public Task<int> ShowChoicePromptAsync(string message, string title, IReadOnlyList<string> buttonLabels)
        {
            LastMessage = message;
            LastButtons = buttonLabels;
            return Task.FromResult(ChoiceToReturn);
        }
    }

    [Fact]
    public async Task PreflightGds_GdsGenerationDisabled_ProceedsWithoutDialog()
    {
        var vm = CreateViewModel();
        vm.GenerateGdsEnabled = false;
        var messageBox = new FakeMessageBoxService();

        var decision = await vm.PreflightGdsAsync(messageBox);

        decision.ShouldBe(GdsPreflightDecision.Proceed);
        messageBox.LastMessage.ShouldBeNull();
    }

    [Fact]
    public async Task PreflightGds_EnvironmentReady_ProceedsWithoutDialog()
    {
        var vm = CreateViewModel();
        vm.GenerateGdsEnabled = true;
        vm.PythonAvailable = true;
        vm.NazcaAvailable = true;
        var messageBox = new FakeMessageBoxService();

        var decision = await vm.PreflightGdsAsync(messageBox);

        decision.ShouldBe(GdsPreflightDecision.Proceed);
        messageBox.LastMessage.ShouldBeNull();
    }

    [Theory]
    [InlineData(0, GdsPreflightDecision.InstallRequested)]
    [InlineData(1, GdsPreflightDecision.OpenSettingsRequested)]
    [InlineData(2, GdsPreflightDecision.SkipGds)]
    [InlineData(-1, GdsPreflightDecision.SkipGds)]   // Dialog geschlossen
    public async Task PreflightGds_NazcaMissing_MapsDialogChoice(int choice, GdsPreflightDecision expected)
    {
        var vm = CreateViewModel();
        vm.GenerateGdsEnabled = true;
        vm.PythonAvailable = true;
        vm.NazcaAvailable = false;
        var messageBox = new FakeMessageBoxService { ChoiceToReturn = choice };

        var decision = await vm.PreflightGdsAsync(messageBox);

        decision.ShouldBe(expected);
        messageBox.LastButtons!.Count.ShouldBe(3);
    }

    [Fact]
    public async Task PreflightGds_NoMessageBoxService_ProceedsLikeBefore()
    {
        // Headless/legacy callers without a dialog service keep the old behavior:
        // attempt the GDS generation and surface the failure in the result.
        var vm = CreateViewModel();
        vm.GenerateGdsEnabled = true;
        vm.NazcaAvailable = false;

        var decision = await vm.PreflightGdsAsync(null);

        decision.ShouldBe(GdsPreflightDecision.Proceed);
    }
}
