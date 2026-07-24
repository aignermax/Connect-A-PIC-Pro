using CAP.Avalonia.Services.AddCustomComponent;
using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// End-to-end lock for the field round-6 fix: the code the editor synthesizes for a bundled
/// SiEPIC component ("Edit Component" → Preview) must actually render in a REAL Python
/// environment via <c>render_gdsfactory_preview.py</c> — the exact pipeline the Preview button
/// drives. Opt-in like the other env-dependent tests: runs only when LUNIMA_TEST_PYTHON3 is
/// set; additionally skips when gdsfactory/ubcpdk are not installed in that interpreter
/// (the CI preview python only carries nazca + klayout + siepic_ebeam_pdk).
/// </summary>
[Trait("Category", "Slow")]
public class SiepicEditPreviewE2ETests
{
    [Fact]
    public async Task SynthesizedEditCode_ForAdiabaticCouplerTe1550_RendersInTheRealEnvironment()
    {
        var python = Environment.GetEnvironmentVariable("LUNIMA_TEST_PYTHON3");
        if (string.IsNullOrWhiteSpace(python)) return;   // env skip
        var script = FindRepoFile(Path.Combine("scripts", "render_gdsfactory_preview.py"));
        if (script is null) return;                       // env skip

        var (pdk, draft) = LoadBundledSiepicDraft("Adiabatic Coupler TE 1550");
        draft.ShouldNotBeNull("'Adiabatic Coupler TE 1550' must exist in the bundled SiEPIC JSON");

        var synthesized = FoundryEditCodeSynthesis.For(
            draft!.GdsFactoryFunction, pdk!.NazcaModuleName, draft.NazcaFunction, draft.NazcaParameters);
        synthesized.ShouldNotBeNull();
        synthesized!.Value.Backend.ShouldBe(GeometryBackend.GdsFactory);

        var service = new GdsFactoryComponentPreviewService(python!, script);
        var result = await service.RenderRawCodeAsync(synthesized.Value.Code);

        // gdsfactory/ubcpdk not installed → env skip, not a Lunima bug.
        if (!result.Success && result.Error?.Contains("No module named") == true) return;

        result.Success.ShouldBeTrue($"synthesized editor code failed to render: {result.Error}");
        result.Polygons.Count.ShouldBeGreaterThan(0, "the rendered cell must carry real geometry");
        result.Pins.Count.ShouldBe(4, "ebeam_adiabatic_te1550 exposes four optical ports");
    }

    private static (PdkDraft? Pdk, PdkComponentDraft? Component) LoadBundledSiepicDraft(string componentName)
    {
        var path = FindRepoFile(Path.Combine("CAP-DataAccess", "PDKs", "siepic-ebeam-pdk.json"));
        if (path is null) return (null, null);
        var pdk = new PdkLoader().LoadFromFileForEditing(path);
        var component = pdk.Components.FirstOrDefault(c =>
            string.Equals(c.Name, componentName, StringComparison.Ordinal));
        return (pdk, component);
    }

    private static string? FindRepoFile(string relativePath)
    {
        var current = new DirectoryInfo(
            Path.GetDirectoryName(typeof(SiepicEditPreviewE2ETests).Assembly.Location)!);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        return null;
    }
}
