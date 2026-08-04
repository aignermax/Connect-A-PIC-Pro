using System.Collections.ObjectModel;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using UnitTests.Export;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Shared fixture behind the GDS round-trip tests (<see cref="GdsUserDesignRoundTripTests"/>,
/// <see cref="GdsHighestLevelRoundTripTests"/>): rebuilds the REAL user design (the one
/// behind the "components are missing after re-import" report) on a fresh canvas — seven
/// components from two bundled PDKs (2× Demo PDK "2x2 MMI Coupler"; SiEPIC "Adiabatic
/// Coupler TE 1550", "Broadband DC TE 1550", 2× "Crossing 4-Port", "DC Halfring-Straight")
/// at his exact coordinates, wired with his ten waveguide connections, routed for real.
/// Also carries the shared harness: nazca-python discovery, the throwaway user-PDK store,
/// and the runtime-registration sink wired like <c>GdsImportServiceTests</c>.
/// <para>
/// Design-build mapping: the user's netlist pin names match the bundled templates
/// verbatim (<c>in1/in2/out1/out2</c> on the MMI, <c>port 1..4</c> on every ebeam
/// cell), so no substitution was needed. The halfring's settings
/// (<c>gap=100E-9,radius=3E-6</c>) are exactly the PDK defaults, so the plain
/// template instance already carries them — no slider fiddling. His 8 external
/// ports are NOT modeled: they are a simulation concept, and the Nazca export only
/// writes top-cell port labels for grating/edge couplers — this design has none, so
/// external ports leave no trace in the GDS either way.
/// </para>
/// </summary>
internal static class GdsUserDesignFixture
{
    /// <summary>
    /// Rebuilds the user's design on a fresh canvas: the seven components at his
    /// exact coordinates, instantiated from the REAL bundled PDK templates, then
    /// his ten waveguide connections, routed for real (the A* grid is initialized
    /// around the design's negative-Y extent like the app does).
    /// </summary>
    public static DesignCanvasViewModel BuildUserDesignCanvas()
    {
        var templates = TestPdkLoader.LoadAllTemplates();
        var canvas = new DesignCanvasViewModel();

        Component Place(string templateName, string pdk, double x, double y)
        {
            var template = templates.First(t => t.Name == templateName && t.PdkSource == pdk);
            var component = ComponentTemplates.CreateFromTemplate(template, x, y);
            canvas.AddComponent(component, templateName, pdk);
            return component;
        }

        const string demo = "Demo PDK";
        const string siepic = "SiEPIC EBeam PDK";
        // His coordinates, verbatim from the exported netlist.
        var mmi1 = Place("2x2 MMI Coupler", demo, 259.699, -513.629);       // _2x2_MMI_Coupler_1
        var mmi2 = Place("2x2 MMI Coupler", demo, 253.899, -397.528);       // _2x2_MMI_Coupler_2
        var adiabatic = Place("Adiabatic Coupler TE 1550", siepic, 298.420, -451.179);
        var bdc = Place("Broadband DC TE 1550", siepic, 730.431, -452.029);
        var crossing872 = Place("Crossing 4-Port", siepic, 542.084, -449.679);
        var crossing1175 = Place("Crossing 4-Port", siepic, 519.699, -449.679);
        var halfring = Place("DC Halfring-Straight", siepic, 267.425, -452.679);

        // His ten connections, verbatim. Pin names match the bundled templates
        // one-to-one (MMI: in1/in2/out1/out2; ebeam: "port N").
        PhysicalPin Pin(Component c, string name) => c.PhysicalPins.First(p => p.Name == name);
        canvas.ConnectPins(Pin(mmi2, "in2"), Pin(mmi1, "in1"));
        canvas.ConnectPins(Pin(mmi1, "in2"), Pin(mmi2, "in1"));
        canvas.ConnectPins(Pin(mmi1, "out2"), Pin(crossing872, "port 4"));
        canvas.ConnectPins(Pin(crossing872, "port 3"), Pin(mmi2, "out1"));
        canvas.ConnectPins(Pin(mmi2, "out2"), Pin(crossing1175, "port 3"));
        canvas.ConnectPins(Pin(crossing1175, "port 4"), Pin(mmi1, "out1"));
        canvas.ConnectPins(Pin(crossing1175, "port 2"), Pin(crossing872, "port 1"));
        canvas.ConnectPins(Pin(crossing872, "port 2"), Pin(bdc, "port 1"));
        canvas.ConnectPins(Pin(adiabatic, "port 4"), Pin(crossing1175, "port 1"));
        canvas.ConnectPins(Pin(halfring, "port 3"), Pin(adiabatic, "port 2"));

        // The app always routes on an initialized grid; the default bounds
        // (-100..5100) would not cover this design's negative-Y extent.
        canvas.InitializeAStarRouting(150, -700, 950, -250);
        canvas.RecalculateRoutesAsync().GetAwaiter().GetResult();
        return canvas;
    }

    /// <summary>Counts the lines of <paramref name="script"/> containing <paramref name="marker"/>.</summary>
    public static int CountLines(string script, string marker) =>
        script.Split('\n').Count(l => l.Contains(marker, StringComparison.Ordinal));

    /// <summary>A throwaway user-PDK store rooted under <paramref name="root"/>.</summary>
    public static UserPdkStore CreateStore(string root, string name) => new(
        Path.Combine(root, name), new PdkJsonSaver(), new PdkLoader());

    /// <summary>
    /// Locates a Python with nazca importable: first a Lunima managed env
    /// (%LOCALAPPDATA%/Lunima/envs/*), then python/python3 on PATH (mirrors
    /// <c>GdsExportFullCircleTests</c>).
    /// </summary>
    public static async Task<string?> FindNazcaPythonAsync()
    {
        var envs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunima", "envs");
        if (Directory.Exists(envs))
        {
            foreach (var root in Directory.GetDirectories(envs))
            {
                foreach (var rel in new[] { Path.Combine("Scripts", "python.exe"), Path.Combine("bin", "python") })
                {
                    var py = Path.Combine(root, rel);
                    if (File.Exists(py) && await ProbeNazca(py))
                        return py;
                }
            }
        }

        foreach (var candidate in new[] { "python", "python3" })
        {
            if (await ProbeNazca(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>True when <paramref name="python"/> starts and can import nazca.</summary>
    public static async Task<bool> ProbeNazca(string python)
    {
        try
        {
            var probe = await SiepicRealGeometryExportTests.RunPythonAsync(
                python, Path.GetTempPath(), "-c", "import nazca");
            return probe.ExitCode == 0;
        }
        catch
        {
            return false;   // not on PATH at all
        }
    }

    /// <summary>Wires the real registrar with throwaway library state (pattern from GdsImportServiceTests).</summary>
    internal sealed class LibrarySink
    {
        public readonly ObservableCollection<ComponentTemplate> Templates = new();
        public readonly ObservableCollection<string> Categories = new();
        public readonly PdkManagerViewModel PdkManager = new();
        public readonly List<PdkDraft> LoadedDrafts = new();
        public readonly UserPreferencesService Preferences;
        public readonly Action<PdkComponentDraft, string, string> Register;

        public LibrarySink(string prefsPath)
        {
            Preferences = new UserPreferencesService(prefsPath);
            var loader = new PdkLoader();
            Register = (draft, pdkName, filePath) =>
                CustomComponentLibraryRegistrar.Register(
                    draft, pdkName, filePath, Templates, Categories, PdkManager,
                    Preferences, loader, LoadedDrafts, () => { }, () => { });
        }
    }
}
