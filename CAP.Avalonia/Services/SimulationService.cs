using System.Numerics;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.ExternalPorts;
using CAP_Core.ExternalPorts.LaserSpectrum;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using CAP_Core.LightCalculation.LaserSpectrum;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Simulation;

namespace CAP.Avalonia.Services;

/// <summary>
/// Orchestrates S-Matrix light simulation from the Avalonia UI state.
/// Bridges the UI (DesignCanvasViewModel) with the core simulation engine
/// (GridLightCalculator) using physical coordinates.
/// Supports per-source wavelength and power configuration.
/// </summary>
public class SimulationService
{
    /// <summary>
    /// Template names that are treated as light input sources.
    /// </summary>
    private static readonly HashSet<string> LightSourceTemplates =
        new(StringComparer.OrdinalIgnoreCase) { "Grating Coupler", "Edge Coupler" };

    /// <summary>
    /// Runs the full S-Matrix simulation and updates the PowerFlowVisualizer.
    /// Supports per-source wavelength/power via LaserConfig on each component.
    /// </summary>
    public async Task<SimulationResult> RunAsync(
        DesignCanvasViewModel canvas,
        CancellationToken cancellationToken = default)
    {
        if (canvas.Components.Count == 0)
            return SimulationResult.Empty("No components placed");

        // Check for connections: either external canvas connections OR internal paths in groups
        var hasConnections = canvas.Connections.Count > 0 || HasInternalPathsInGroups(canvas);
        if (!hasConnections)
            return SimulationResult.Empty("No connections");

        var tileManager = new ComponentListTileManager();
        foreach (var compVm in canvas.Components)
        {
            // Ensure ComponentGroups have computed S-Matrices before simulation
            if (compVm.Component is ComponentGroup group)
            {
                group.EnsureSMatrixComputed();
            }
            tileManager.AddComponent(compVm.Component);
        }

        var portManager = new PhysicalExternalPortManager();
        var sourceConfigs = ConfigureLightSources(canvas, portManager);

        if (sourceConfigs.Count == 0)
            return SimulationResult.Empty(
                "No light sources found (place a Grating Coupler or Edge Coupler)");

        var gridManager = GridManager.CreateForSimulation(
            tileManager, canvas.ConnectionManager, portManager);

        // Run simulation for each distinct wavelength sample. Sources with a finite
        // linewidth (#819) contribute several weighted samples around their center.
        var wavelengths = sourceConfigs.Select(s => s.WavelengthNm).Distinct().ToList();
        var runWavelengths = sourceConfigs
            .SelectMany(s => s.SampleWavelengthsNm).Distinct().ToList();
        bool anySpectral = sourceConfigs.Any(s => s.HasSpectralLinewidth);
        var perWavelengthFields = new List<Dictionary<Guid, Complex>>();
        SMatrix? systemMatrix = null;

        foreach (var wl in runWavelengths)
        {
            var builder = new SystemMatrixBuilder(gridManager);
            var calculator = new GridLightCalculator(builder, gridManager);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var fields = await calculator.CalculateFieldPropagationAsync(cts, wl);
            perWavelengthFields.Add(fields);

            // Capture the system S-Matrix from the first wavelength for diagnostics
            if (systemMatrix == null)
            {
                systemMatrix = builder.GetSystemSMatrix(wl);
            }
        }

        // Spectral samples of one source add incoherently (power sum). Distinct
        // ideal sources keep the legacy complex merge so existing results are
        // reproduced exactly.
        var allFieldResults = anySpectral
            ? IncoherentFieldCombiner.Combine(perWavelengthFields)
            : MergeAllFieldResults(perWavelengthFields);

        var components = canvas.Components.Select(c => c.Component).ToList();
        canvas.PowerFlowVisualizer.UpdateFromSimulation(
            canvas.ConnectionManager.Connections, components, allFieldResults);

        canvas.RefreshPowerFlowDisplay();

        return new SimulationResult
        {
            Success = true,
            FieldResults = allFieldResults,
            WavelengthsUsed = wavelengths,
            LightSourceCount = sourceConfigs.Count,
            ComponentCount = canvas.Components.Count,
            ConnectionCount = canvas.Connections.Count,
            SourceConfigs = sourceConfigs,
            SystemMatrix = systemMatrix
        };
    }

    /// <summary>
    /// Finds I/O components and configures them as light sources.
    /// Uses per-source LaserConfig when available, otherwise defaults.
    /// Recursively searches inside ComponentGroups.
    /// </summary>
    internal List<SourceConfigInfo> ConfigureLightSources(
        DesignCanvasViewModel canvas,
        PhysicalExternalPortManager portManager)
    {
        var configs = new List<SourceConfigInfo>();

        // Per-instance LaserConfig only exists on top-level ViewModels; components
        // inside groups fall back to the default (ideal red) source.
        var laserConfigs = new Dictionary<Component, LaserConfig>();
        foreach (var compVm in canvas.Components)
        {
            if (compVm.LaserConfig != null)
                laserConfigs[compVm.Component] = compVm.LaserConfig;
        }

        // Collect all components, including those inside groups (recursively)
        var allComponents = GetAllComponentsRecursively(canvas.Components);

        foreach (var component in allComponents)
        {
            // ONA Analyzer: its "source" pin emits in the regular simulation too,
            // so light flows visibly source → DUT → measurement (consistent with the
            // ONA sweep). Only the source pin emits; measurement stays a detector.
            var analyzerSource = GetAnalyzerLightSourcePin(component);
            if (analyzerSource != null)
            {
                int analyzerWavelengthNm = StandardWaveLengths.RedNM;
                portManager.AddLightSource(
                    new ExternalInput(
                        $"ona_{component.Identifier}_source",
                        GetLaserTypeForWavelength(analyzerWavelengthNm),
                        0,
                        new Complex(1.0, 0)),
                    analyzerSource.LogicalPin!.IDInFlow);
                configs.Add(new SourceConfigInfo(component.Identifier, analyzerWavelengthNm, 1.0));
                continue;
            }

            if (!IsLightSource(component))
                continue;

            // Couplers whose laser is switched off (#690) act as listen-only outputs.
            // The flag lives on the core component, so it also covers group children.
            if (!component.LaserEnabled)
                continue;

            // Use the per-instance LaserConfig when available; components inside
            // groups have no ViewModel, so they keep the default (ideal red) source.
            var config = laserConfigs.GetValueOrDefault(component);
            int wavelengthNm = config?.WavelengthNm ?? StandardWaveLengths.RedNM;
            double power = config?.InputPower ?? 1.0;
            var spectrum = config?.ToSpectrum() ?? new LaserSpectrumModel(wavelengthNm);
            var samples = spectrum.GetSamples();
            var sampleWavelengths = samples.Select(s => s.WavelengthNm).ToList();

            foreach (var pin in component.PhysicalPins)
            {
                if (pin.LogicalPin?.MatterType != MatterType.Light)
                    continue;

                foreach (var sample in samples)
                {
                    // The center sample keeps the legacy name; side samples are suffixed.
                    string name = sample.WavelengthNm == wavelengthNm
                        ? $"src_{component.Identifier}_{pin.Name}"
                        : $"src_{component.Identifier}_{pin.Name}_{sample.WavelengthNm}nm";
                    var input = new ExternalInput(
                        name,
                        GetLaserTypeForWavelength(sample.WavelengthNm),
                        0,
                        new Complex(power * sample.Weight, 0));
                    portManager.AddLightSource(input, pin.LogicalPin.IDInFlow);
                }

                configs.Add(new SourceConfigInfo(
                    component.Identifier, wavelengthNm, power, sampleWavelengths));
            }
        }

        return configs;
    }

    /// <summary>
    /// Recursively collects all Component instances, including those inside ComponentGroups.
    /// Returns the raw Component objects, not ViewModels, to avoid TemplateName issues.
    /// </summary>
    public static List<Component> GetAllComponentsRecursively(IEnumerable<ComponentViewModel> components)
    {
        var result = new List<Component>();

        foreach (var compVm in components)
        {
            // Add the component itself
            result.Add(compVm.Component);

            // If it's a group, recursively add its children
            if (compVm.Component is ComponentGroup group)
            {
                result.AddRange(GetAllComponentsFromGroup(group));
            }
        }

        return result;
    }

    /// <summary>
    /// Recursively extracts all components from a ComponentGroup.
    /// </summary>
    private static List<Component> GetAllComponentsFromGroup(ComponentGroup group)
    {
        var result = new List<Component>();

        foreach (var child in group.ChildComponents)
        {
            result.Add(child);

            if (child is ComponentGroup childGroup)
            {
                result.AddRange(GetAllComponentsFromGroup(childGroup));
            }
        }

        return result;
    }

    /// <summary>
    /// Checks if any ComponentGroups in the canvas have internal paths (connections).
    /// When all components are grouped, canvas.Connections is empty but groups have InternalPaths.
    /// </summary>
    private static bool HasInternalPathsInGroups(DesignCanvasViewModel canvas)
    {
        foreach (var compVm in canvas.Components)
        {
            if (compVm.Component is ComponentGroup group)
            {
                if (group.InternalPaths.Count > 0)
                    return true;

                // Recursively check nested groups
                if (HasInternalPathsInGroupRecursive(group))
                    return true;
            }
        }
        return false;
    }

    private static bool HasInternalPathsInGroupRecursive(ComponentGroup group)
    {
        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup nestedGroup)
            {
                if (nestedGroup.InternalPaths.Count > 0)
                    return true;
                if (HasInternalPathsInGroupRecursive(nestedGroup))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns the ONA Analyzer's "source" pin when <paramref name="component"/> is an
    /// analysis tool with a usable light source pin; otherwise null. The regular
    /// simulation injects light here so the analyzer behaves like its sweep mode:
    /// light flows source → DUT → measurement. The measurement pin stays a detector.
    /// </summary>
    public static PhysicalPin? GetAnalyzerLightSourcePin(Component component)
    {
        if (!component.IsAnalysisTool)
            return null;

        var sourcePin = component.PhysicalPins.FirstOrDefault(
            p => string.Equals(p.Name, "source", StringComparison.OrdinalIgnoreCase));

        return sourcePin?.LogicalPin?.MatterType == MatterType.Light ? sourcePin : null;
    }

    public static bool IsLightSource(Component component) =>
        LightSourceClassifier.IsLightInjectingCoupler(component)
        || component.IsUserMarkedLightSource;

    internal static LaserType GetLaserTypeForWavelength(int wavelengthNm)
    {
        if (wavelengthNm == StandardWaveLengths.RedNM) return LaserType.Red;
        if (wavelengthNm == StandardWaveLengths.GreenNM) return LaserType.Green;
        if (wavelengthNm == StandardWaveLengths.BlueNM) return LaserType.Blue;
        // Spectral samples (#819) sit at arbitrary wavelengths; hue follows the
        // nearest standard wavelength.
        return new LaserType(NearestStandardColor(wavelengthNm), wavelengthNm);
    }

    private static LightColor NearestStandardColor(int wavelengthNm)
    {
        int redDist = Math.Abs(wavelengthNm - StandardWaveLengths.RedNM);
        int greenDist = Math.Abs(wavelengthNm - StandardWaveLengths.GreenNM);
        int blueDist = Math.Abs(wavelengthNm - StandardWaveLengths.BlueNM);
        if (redDist <= greenDist && redDist <= blueDist) return LightColor.Red;
        return greenDist <= blueDist ? LightColor.Green : LightColor.Blue;
    }

    /// <summary>Legacy multi-wavelength merge: complex sum per pin (pre-#819 behaviour).</summary>
    private static Dictionary<Guid, Complex> MergeAllFieldResults(
        IReadOnlyList<Dictionary<Guid, Complex>> perWavelengthFields)
    {
        var target = new Dictionary<Guid, Complex>();
        foreach (var fields in perWavelengthFields)
        {
            foreach (var kvp in fields)
            {
                if (target.ContainsKey(kvp.Key))
                    target[kvp.Key] += kvp.Value;
                else
                    target[kvp.Key] = kvp.Value;
            }
        }
        return target;
    }
}

public class SimulationResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public Dictionary<Guid, Complex>? FieldResults { get; set; }
    public List<int> WavelengthsUsed { get; set; } = new();
    public int LightSourceCount { get; set; }
    public int ComponentCount { get; set; }
    public int ConnectionCount { get; set; }
    public List<SourceConfigInfo> SourceConfigs { get; set; } = new();
    public SMatrix? SystemMatrix { get; set; }

    public static SimulationResult Empty(string message) => new()
    {
        Success = false,
        ErrorMessage = message
    };

    public string WavelengthSummary =>
        WavelengthsUsed.Count > 0
            ? string.Join(", ", WavelengthsUsed.Select(w => $"{w}nm"))
            : "none";
}
