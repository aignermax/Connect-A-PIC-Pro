using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Export;
using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// The unified component editor's S-matrix flow:
/// "Compute with Meep" must not require a manual Preview click first, computed
/// matrices must land in the PDK JSON on save, and an edit-save without a fresh
/// compute must not silently wipe the S-matrix already stored in the definition.
/// </summary>
public class EditorSMatrixFlowTests : IDisposable
{
    static EditorSMatrixFlowTests() =>
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-smx-flow-" + Guid.NewGuid().ToString("N"));

    private const string CouplerCode = "import gdsfactory as gf\ncomponent = gf.components.coupler()";

    private static NazcaPreviewResult RenderOk() => new()
    {
        Success = true, XMin = 0, YMin = 0, XMax = 10, YMax = 2,
        Pins = new List<NazcaPreviewPin> { new() { Name = "o1", X = 0, Y = 1, Angle = 180 }, new() { Name = "o2", X = 10, Y = 1, Angle = 0 } }
    };

    private static FdtdSMatrixResult SolveOk() => new()
    {
        Success = true,
        Ports = new[] { "o1", "o2" },
        Wavelengths = new[] { 1.55 },
        Entries = new[]
        {
            new FdtdSEntry { Key = "o2@0,o1@0", Values = new[] { new Complex(0.95, 0.0) } },
            new FdtdSEntry { Key = "o1@0,o2@0", Values = new[] { new Complex(0.95, 0.0) } },
        },
    };

    private static PdkSMatrixDraft StoredSMatrix(double magnitude) => new()
    {
        WavelengthNm = 1310,
        WavelengthData = new List<WavelengthSMatrixEntry>
        {
            new()
            {
                WavelengthNm = 1310,
                Connections = new List<SMatrixConnection>
                {
                    new() { FromPin = "o1", ToPin = "o2", Magnitude = magnitude, PhaseDegrees = 0 },
                }
            }
        }
    };

    private (NewComponentViewModel vm, Mock<IFdtdSMatrixService> fdtd, Mock<IComponentPreviewRenderer> gds, UserPdkStore store)
        Build(bool renderSucceeds = true, bool withSecondPdk = false)
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(renderSucceeds ? RenderOk() : new NazcaPreviewResult { Success = false, Error = "render exploded" });
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var fdtd = new Mock<IFdtdSMatrixService>();
        fdtd.Setup(f => f.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdtdAvailability.Available(""));
        fdtd.Setup(f => f.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SolveOk());

        var store = new UserPdkStore(_root, new PdkJsonSaver(), new PdkLoader());
        var process = new ProcessDefinition { Name = "P" };
        store.SaveToNamedPdk("Lib", process, SeedComponent("comp1", StoredSMatrix(0.8)), "gdsfactory", null);
        if (withSecondPdk)
        {
            // Same process NAME, different PDK file — a migration target that merely
            // shares the process name with the source PDK.
            store.SaveToNamedPdk("Lib2", new ProcessDefinition { Name = "P" }, SeedComponent("other"), "gdsfactory", null);
        }

        var vm = new NewComponentViewModel(extractor, fdtd.Object, store, new List<ProcessDefinition> { process })
        {
            ComponentName = "My Comp",
            SelectedBackend = GeometryBackend.GdsFactory,
            Code = CouplerCode,
        };
        return (vm, fdtd, gds, store);
    }

    private static PdkComponentDraft SeedComponent(string name, PdkSMatrixDraft? sMatrix = null) => new()
    {
        Name = name, WidthMicrometers = 5, HeightMicrometers = 1,
        RawCode = CouplerCode, RawCodeBackend = "gdsfactory",
        SMatrix = sMatrix,
        Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } }
    };

    private ComponentTemplate EditTemplate(PdkComponentDraft sourceDraft) => new()
    {
        Name = sourceDraft.Name,
        RawCode = sourceDraft.RawCode,
        RawCodeBackend = sourceDraft.RawCodeBackend,
        PdkSource = "Lib",
        SourceDraft = sourceDraft,
    };

    [Fact]
    public async Task ComputeSMatrix_withoutPriorPreview_rendersTheGeometryItself_andComputes()
    {
        var (vm, fdtd, _, _) = Build();

        // No RunPreviewCommand — "Compute with Meep" is clicked straight away.
        await vm.ComputeSMatrixCommand.ExecuteAsync(null);

        fdtd.Verify(f => f.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
        vm.HasPreview.ShouldBeTrue();
        vm.HasSMatrix.ShouldBeTrue();
        vm.StatusText.ShouldContain("computed");
    }

    [Fact]
    public async Task ComputeSMatrix_whenTheRenderFails_reportsTheErrorAndNeverSolves()
    {
        var (vm, fdtd, _, _) = Build(renderSucceeds: false);

        await vm.ComputeSMatrixCommand.ExecuteAsync(null);

        fdtd.Verify(f => f.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
        vm.StatusText.ShouldContain("render exploded");
        vm.HasSMatrix.ShouldBeFalse();
    }

    [Fact]
    public async Task ComputeSMatrix_successStatus_pointsAtSaveChangesAsThePersistenceStep()
    {
        var (vm, _, _, _) = Build();

        await vm.ComputeSMatrixCommand.ExecuteAsync(null);

        vm.StatusText.ShouldContain("Save");
    }

    [Fact]
    public async Task Save_afterCompute_persistsTheComputedMatricesIntoThePdkJson()
    {
        var (vm, _, _, _) = Build();

        await vm.ComputeSMatrixCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedFilePath.ShouldNotBeNull();
        var persisted = new PdkLoader().LoadFromFileForEditing(vm.SavedFilePath!);
        var comp = persisted.Components.Single(c => c.Name == "My Comp");
        comp.SMatrix.ShouldNotBeNull();
        var wl = comp.SMatrix!.WavelengthData.ShouldNotBeNull();
        wl.ShouldContain(e => e.WavelengthNm == 1550);
        wl.Single(e => e.WavelengthNm == 1550).Connections
            .ShouldContain(c => Math.Abs(c.Magnitude - 0.95) < 1e-6);
    }

    [Fact]
    public async Task EditSave_withoutRecompute_keepsTheStoredSMatrixOfTheDefinition()
    {
        var (vm, _, _, store) = Build();
        var seeded = new PdkLoader().LoadFromFileForEditing(store.ListCustomPdks().Single().FilePath)
            .Components.Single(c => c.Name == "comp1");

        vm.LoadForEdit(EditTemplate(seeded)).ShouldBeTrue();
        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedDraft.ShouldNotBeNull();
        vm.SavedDraft!.SMatrix.ShouldNotBeNull();
        vm.SavedDraft.SMatrix!.WavelengthData!.Single().Connections
            .ShouldContain(c => Math.Abs(c.Magnitude - 0.8) < 1e-6);

        var persisted = new PdkLoader().LoadFromFileForEditing(vm.SavedFilePath!)
            .Components.Single(c => c.Name == "comp1");
        persisted.SMatrix.ShouldNotBeNull();
    }

    [Fact]
    public async Task EditSave_afterAGeometryCodeChange_dropsTheStaleStoredSMatrix()
    {
        var (vm, _, _, store) = Build();
        var seeded = new PdkLoader().LoadFromFileForEditing(store.ListCustomPdks().Single().FilePath)
            .Components.Single(c => c.Name == "comp1");

        vm.LoadForEdit(EditTemplate(seeded)).ShouldBeTrue();
        vm.Code = "import gdsfactory as gf\ncomponent = gf.components.mmi1x2()";
        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedDraft.ShouldNotBeNull();
        vm.SavedDraft!.SMatrix.ShouldBeNull();
    }

    [Fact]
    public async Task EditSave_movedToAnotherPdk_dropsTheStoredSMatrix_toBlackBox()
    {
        var (vm, _, _, store) = Build(withSecondPdk: true);
        var seeded = new PdkLoader().LoadFromFileForEditing(
                store.ListCustomPdks().Single(p => p.Name == "Lib").FilePath)
            .Components.Single(c => c.Name == "comp1");

        vm.LoadForEdit(EditTemplate(seeded)).ShouldBeTrue();
        vm.SelectedPdkChoice = vm.PdkChoices.First(c => !c.IsNewPdk && c.Pdk!.Name == "Lib2");
        await vm.SaveCommand.ExecuteAsync(null);

        // The stored matrix was computed under Lib's process definition. Even though Lib2's
        // process shares the NAME "P", it is a different PDK — carrying the matrix over
        // verbatim would be invented physics (#582 stale rule).
        vm.SavedDraft.ShouldNotBeNull();
        vm.SavedDraft!.SMatrix.ShouldBeNull();
        vm.StatusText.ShouldContain("Moved");
    }

    [Fact]
    public async Task EditSave_ofAJsonDefinedComponent_whoseRenderedPinsDoNotMatchTheStoredMatrix_dropsIt()
    {
        var (vm, _, _, _) = Build();

        // A foundry-style JSON definition: no RawCode (the editor synthesizes code from the
        // function reference), stored matrix referencing pins opt1/opt2 — but rendering the
        // synthesized code yields pins o1/o2 (RenderOk). Keeping the matrix would persist
        // connections against pins the saved draft does not define -> silent zero physics later.
        var mismatchedMatrix = new PdkSMatrixDraft
        {
            WavelengthNm = 1310,
            WavelengthData = new List<WavelengthSMatrixEntry>
            {
                new()
                {
                    WavelengthNm = 1310,
                    Connections = new List<SMatrixConnection>
                    {
                        new() { FromPin = "opt1", ToPin = "opt2", Magnitude = 0.8, PhaseDegrees = 0 },
                    }
                }
            }
        };
        var template = new ComponentTemplate
        {
            Name = "comp1",
            PdkSource = "Lib",
            GdsFactoryFunction = "coupler",
            SourceDraft = new PdkComponentDraft
            {
                Name = "comp1", WidthMicrometers = 5, HeightMicrometers = 1,
                SMatrix = mismatchedMatrix,
                Pins = new() { new PhysicalPinDraft { Name = "opt1" }, new PhysicalPinDraft { Name = "opt2" } }
            },
        };

        vm.LoadForEdit(template).ShouldBeTrue();
        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedDraft.ShouldNotBeNull();
        vm.SavedDraft!.SMatrix.ShouldBeNull();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
