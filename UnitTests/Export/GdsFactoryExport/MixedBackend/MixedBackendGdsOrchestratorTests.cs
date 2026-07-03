using CAP.Avalonia.Services.GdsFactoryExport;
using CAP.Avalonia.Services.GdsFactoryExport.MixedBackend;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Export;
using CAP_DataAccess.Persistence.PIR;
using Moq;
using Shouldly;

namespace UnitTests.Export.GdsFactoryExport.MixedBackend;

/// <summary>
/// Tests for the mixed-backend orchestration (issue #646): Nazca part first,
/// gdsfactory host second, one composed GDS at the end.
/// </summary>
public class MixedBackendGdsOrchestratorTests : IDisposable
{
    private readonly string _dir;

    public MixedBackendGdsOrchestratorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mixed-gds-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static DesignCanvasViewModel CreateMixedCanvas(out Dictionary<string, NazcaCodeOverride> overrides)
    {
        var canvas = new DesignCanvasViewModel();
        foreach (var id in new[] { "NazcaOvr", "GfOvr" })
        {
            var component = TestComponentFactory.CreateBasicComponent();
            component.Identifier = id;
            component.NazcaFunctionName = "ebeam_y_1550";
            canvas.AddComponent(component, id);
        }
        overrides = new Dictionary<string, NazcaCodeOverride>
        {
            ["NazcaOvr"] = new() { RawCode = "component = my_cell", Backend = OverrideBackend.Nazca },
            ["GfOvr"] = new() { RawCode = "component = gf.components.mmi1x2()", Backend = OverrideBackend.GdsFactory },
        };
        return canvas;
    }

    private static Mock<GdsExportService> CreateServiceMock(bool partSucceeds = true)
    {
        var mock = new Mock<GdsExportService>(null!, null!);
        mock.Setup(s => s.ExportToGdsAsync(It.IsAny<string>(), true))
            .ReturnsAsync((string scriptPath, bool _) =>
            {
                var isPart = scriptPath.Contains(MixedBackendGdsOrchestrator.NazcaPartSuffix, StringComparison.Ordinal);
                if (isPart && !partSucceeds)
                    return new GdsExportService.ExportResult
                    {
                        ScriptPath = scriptPath,
                        Success = false,
                        ErrorMessage = "No module named 'nazca'",
                    };
                return new GdsExportService.ExportResult
                {
                    ScriptPath = scriptPath,
                    GdsPath = Path.ChangeExtension(scriptPath, ".gds"),
                    Success = true,
                };
            });
        return mock;
    }

    [Fact]
    public void RequiresMixedExport_TrueOnlyWithNazcaBackendOverride()
    {
        var canvas = CreateMixedCanvas(out var overrides);

        MixedBackendGdsOrchestrator.RequiresMixedExport(canvas, overrides).ShouldBeTrue();

        overrides.Remove("NazcaOvr");
        MixedBackendGdsOrchestrator.RequiresMixedExport(canvas, overrides).ShouldBeFalse();
        MixedBackendGdsOrchestrator.RequiresMixedExport(canvas, null).ShouldBeFalse();
    }

    [Fact]
    public async Task ExportMixed_RunsNazcaPartThenHost_AndComposesOneGds()
    {
        var canvas = CreateMixedCanvas(out var overrides);
        var service = CreateServiceMock();
        var orchestrator = new MixedBackendGdsOrchestrator(service.Object);
        var hostScript = Path.Combine(_dir, "design.py");
        var partScript = MixedBackendGdsOrchestrator.GetNazcaPartScriptPath(hostScript);

        var result = await orchestrator.ExportMixedAsync(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.UbcPdkCells),
            overrides, hostScript);

        result.Success.ShouldBeTrue();
        result.GdsPath.ShouldBe(Path.ChangeExtension(hostScript, ".gds"));

        // Both scripts were written and run, part before host.
        var partContent = await File.ReadAllTextAsync(partScript);
        partContent.ShouldContain("import nazca as nd");
        partContent.ShouldContain("_ovr_NazcaOvr");
        var hostContent = await File.ReadAllTextAsync(hostScript);
        hostContent.ShouldContain("gf.import_gds(");
        hostContent.ShouldContain(Path.GetFileNameWithoutExtension(partScript) + ".gds");
        hostContent.ShouldContain("def override_GfOvr(");   // gf-backend override honoured in host
        hostContent.ShouldNotContain("# NazcaOvr");         // merged instance not double-placed
        service.Verify(s => s.ExportToGdsAsync(partScript, true), Times.Once);
        service.Verify(s => s.ExportToGdsAsync(hostScript, true), Times.Once);
    }

    [Fact]
    public async Task ExportMixed_NazcaPartFailure_ReturnsFailureWithoutRunningHost()
    {
        var canvas = CreateMixedCanvas(out var overrides);
        var service = CreateServiceMock(partSucceeds: false);
        var orchestrator = new MixedBackendGdsOrchestrator(service.Object);
        var hostScript = Path.Combine(_dir, "design.py");

        var result = await orchestrator.ExportMixedAsync(
            canvas, new GdsFactoryExportOptions(GdsFactoryComponentMode.UbcPdkCells),
            overrides, hostScript);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("Nazca-backend overrides failed");
        File.Exists(hostScript).ShouldBeFalse();   // host phase never started
        service.Verify(s => s.ExportToGdsAsync(hostScript, true), Times.Never);
    }
}
