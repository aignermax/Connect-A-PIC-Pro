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

    [Fact]
    public void RebuildInterpreterOptions_MergesManagedAndDiscovered_WithActiveMarking()
    {
        var vm = CreateViewModel();
        vm.CustomPythonPath = "/envs/nazca/bin/python";
        vm.ManagedEnvironmentsProvider = () => new[]
        {
            new ManagedEnvCandidate("nazca", "/envs/nazca/bin/python", "Managed · nazca"),
        };
        vm.AvailablePythons.Add(new PythonDiscoveryService.PythonInstallation
        {
            Path = "/usr/bin/python3",
            Source = "System",
            PythonVersion = "3.12.1",
            NazcaVersion = "0.6.1",
        });

        vm.RefreshManagedCandidates();

        vm.InterpreterOptions.Count.ShouldBe(2);
        vm.InterpreterOptions[0].Path.ShouldBe("/envs/nazca/bin/python");
        vm.InterpreterOptions[0].IsActive.ShouldBeTrue();      // matches CustomPythonPath
        vm.InterpreterOptions[0].ManagedName.ShouldBe("nazca");
        vm.InterpreterOptions[1].Path.ShouldBe("/usr/bin/python3");
        vm.InterpreterOptions[1].IsActive.ShouldBeFalse();
        vm.InterpreterOptions[1].ManagedName.ShouldBeNull();
    }

    [Fact]
    public async Task SelectInterpreter_ManagedOption_ActivatesViaRegistryDelegate()
    {
        var vm = CreateViewModel();
        string? activated = null;
        vm.ActivateManagedEnvironment = name => activated = name;
        var option = new InterpreterOption("Managed · nazca", "/envs/nazca/bin/python", false, "nazca");

        await vm.SelectInterpreterCommand.ExecuteAsync(option);

        activated.ShouldBe("nazca");
    }

    [Fact]
    public async Task SelectInterpreter_SystemOption_SetsPathAndPersists()
    {
        var vm = CreateViewModel();
        string? persisted = null;
        vm.OnPythonPathChanged = p => persisted = p;
        var option = new InterpreterOption("System · 3.12", "/usr/bin/python3", false, null);

        await vm.SelectInterpreterCommand.ExecuteAsync(option);

        vm.CustomPythonPath.ShouldBe("/usr/bin/python3");
        persisted.ShouldBe("/usr/bin/python3");
    }

    [Fact]
    public void RefreshManagedCandidates_NoManagedEnvs_ShowsCreateEnvironmentButton()
    {
        var vm = CreateViewModel();
        vm.NazcaAvailable = true;   // auch mit System-Nazca soll der Create-Button sichtbar bleiben
        vm.ManagedEnvironmentsProvider = () => Array.Empty<ManagedEnvCandidate>();

        vm.RefreshManagedCandidates();

        vm.ShowCreateEnvironmentButton.ShouldBeTrue();
        vm.ShowNazcaInstallOffer.ShouldBeFalse();   // Warnbanner nur ohne jegliches Nazca
    }

    [Fact]
    public void RefreshManagedCandidates_ManagedEnvExists_HidesCreateEnvironmentButton()
    {
        var vm = CreateViewModel();
        vm.ManagedEnvironmentsProvider = () => new[]
        {
            new ManagedEnvCandidate("nazca", "/envs/nazca/bin/python", "Managed · nazca"),
        };

        vm.RefreshManagedCandidates();

        vm.ShowCreateEnvironmentButton.ShouldBeFalse();
    }

    /// <summary>Test double with hand-controlled discovery completion, to interleave runs.</summary>
    private sealed class ControllableDiscoveryViewModel : GdsExportViewModel
    {
        public Queue<TaskCompletionSource<List<PythonDiscoveryService.PythonInstallation>>> PendingDiscoveries { get; } = new();

        public ControllableDiscoveryViewModel() : base(new GdsExportService()) { }

        protected override Task<List<PythonDiscoveryService.PythonInstallation>> DiscoverPythonsAsync()
        {
            var tcs = new TaskCompletionSource<List<PythonDiscoveryService.PythonInstallation>>();
            PendingDiscoveries.Enqueue(tcs);
            return tcs.Task;
        }
    }

    private static List<PythonDiscoveryService.PythonInstallation> OneSystemPython() => new()
    {
        new PythonDiscoveryService.PythonInstallation
        {
            Path = "/usr/bin/python3",
            Source = "System",
            PythonVersion = "3.12.1",
            NazcaVersion = "0.6.1",
        },
    };

    [Fact]
    public async Task SearchForPython_OverlappingRuns_DoNotDuplicateResults()
    {
        // Navigating GDS Export → elsewhere → GDS Export re-triggers the search while the
        // first run is still in flight; both used to append their results (2 → 4 → 8 …).
        var vm = new ControllableDiscoveryViewModel();
        vm.CustomPythonPath = "/some/python";   // skip the auto-select side path

        var first = vm.SearchForPythonAsync();
        var second = vm.SearchForPythonAsync();

        vm.PendingDiscoveries.Dequeue().SetResult(OneSystemPython());   // first finishes late
        vm.PendingDiscoveries.Dequeue().SetResult(OneSystemPython());
        await first;
        await second;

        vm.AvailablePythons.Count.ShouldBe(1);   // nur der neueste Lauf zählt
        vm.IsSearching.ShouldBeFalse();
    }

    [Fact]
    public async Task SearchForPython_SupersededRun_DoesNotOverwriteNewerResults()
    {
        var vm = new ControllableDiscoveryViewModel();
        vm.CustomPythonPath = "/some/python";

        var first = vm.SearchForPythonAsync();
        var second = vm.SearchForPythonAsync();

        // Der NEUERE Lauf kommt zuerst zurück, der alte danach — das alte Ergebnis
        // darf das neue nicht mehr überschreiben oder ergänzen.
        vm.PendingDiscoveries.ToArray()[1].SetResult(OneSystemPython());
        await second;
        vm.PendingDiscoveries.Dequeue().SetResult(new List<PythonDiscoveryService.PythonInstallation>());
        await first;

        vm.AvailablePythons.Count.ShouldBe(1);
        vm.IsSearching.ShouldBeFalse();
    }

    [Fact]
    public void Initialize_NullPath_ClearsConfiguredInterpreter()
    {
        // Deleting the active managed environment pushes null through the registry
        // callback — the stale (deleted) interpreter path must not survive that.
        var service = new GdsExportService();
        var vm = new GdsExportViewModel(service);
        vm.Initialize("/envs/deleted/bin/python");

        vm.Initialize(null);

        vm.CustomPythonPath.ShouldBeEmpty();
        vm.PythonPathSource.ShouldBeEmpty();
    }

    [Fact]
    public async Task TrySelectFallback_NoInterpreterConfigured_PicksFirstCandidate()
    {
        var vm = CreateViewModel();
        string? activated = null;
        vm.ActivateManagedEnvironment = name => activated = name;
        vm.ManagedEnvironmentsProvider = () => new[]
        {
            new ManagedEnvCandidate("backup-env", "/envs/backup/bin/python", "Managed · backup-env"),
        };
        vm.RefreshManagedCandidates();

        await vm.TrySelectFallbackInterpreterAsync();

        activated.ShouldBe("backup-env");   // erster Kandidat wird automatisch übernommen
    }

    [Fact]
    public async Task TrySelectFallback_ConfiguredInterpreterStillExists_DoesNothing()
    {
        var existing = Path.GetTempFileName();
        try
        {
            var vm = CreateViewModel();
            string? activated = null;
            vm.ActivateManagedEnvironment = name => activated = name;
            vm.CustomPythonPath = existing;
            vm.ManagedEnvironmentsProvider = () => new[]
            {
                new ManagedEnvCandidate("other", "/envs/other/bin/python", "Managed · other"),
            };
            vm.RefreshManagedCandidates();

            await vm.TrySelectFallbackInterpreterAsync();

            activated.ShouldBeNull();       // funktionierende Auswahl bleibt unangetastet
        }
        finally
        {
            File.Delete(existing);
        }
    }

    [Fact]
    public async Task TrySelectFallback_ConfiguredInterpreterDeleted_PicksCandidate()
    {
        var vm = CreateViewModel();
        string? activated = null;
        vm.ActivateManagedEnvironment = name => activated = name;
        vm.CustomPythonPath = Path.Combine(Path.GetTempPath(), "deleted-env", "python.exe");
        vm.ManagedEnvironmentsProvider = () => new[]
        {
            new ManagedEnvCandidate("backup-env", "/envs/backup/bin/python", "Managed · backup-env"),
        };
        vm.RefreshManagedCandidates();

        await vm.TrySelectFallbackInterpreterAsync();

        activated.ShouldBe("backup-env");
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
