using System.Numerics;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.ExternalPorts;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using CAP.Avalonia.ViewModels.Canvas;

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

        // Run simulation for each distinct wavelength
        var wavelengths = sourceConfigs.Select(s => s.WavelengthNm).Distinct().ToList();
        var allFieldResults = new Dictionary<Guid, Complex>();
        SMatrix? systemMatrix = null;

        foreach (var wl in wavelengths)
        {
            var builder = new SystemMatrixBuilder(gridManager);
            var calculator = new GridLightCalculator(builder, gridManager);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var fields = await calculator.CalculateFieldPropagationAsync(cts, wl);
            MergeFieldResults(allFieldResults, fields);

            // Capture the system S-Matrix from the first wavelength for diagnostics
            if (systemMatrix == null)
            {
                systemMatrix = builder.GetSystemSMatrix(wl);
            }
        }

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

        // Couplers whose laser is switched off (#690) act as listen-only outputs.
        // LaserConfig lives on the top-level ViewModel, so only top-level couplers
        // can be disabled; components inside groups keep the default (enabled).
        var disabledLasers = canvas.Components
            .Where(c => c.LaserConfig is { IsEnabled: false })
            .Select(c => c.Component)
            .ToHashSet();

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

            if (disabledLasers.Contains(component))
                continue;

            // For components inside groups, we don't have LaserConfig, so use defaults
            int wavelengthNm = StandardWaveLengths.RedNM;
            double power = 1.0;
            var laserType = GetLaserTypeForWavelength(wavelengthNm);

            foreach (var pin in component.PhysicalPins)
            {
                if (pin.LogicalPin?.MatterType != MatterType.Light)
                    continue;

                var input = new ExternalInput(
                    $"src_{component.Identifier}_{pin.Name}",
                    laserType,
                    0,
                    new Complex(power, 0));

                portManager.AddLightSource(input, pin.LogicalPin.IDInFlow);
                configs.Add(new SourceConfigInfo(
                    component.Identifier, wavelengthNm, power));
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

    public static bool IsLightSource(Component component)
    {
        var id = component.Identifier?.ToLowerInvariant() ?? "";
        if (id.Contains("grating") || id.Contains("edge coupler"))
            return true;

        // Check NazcaFunctionName (e.g., "ebeam_gc_te1550") — this is reliable because
        // it comes from the PDK and is not user-editable (unlike HumanReadableName).
        // After prefab serialize/deserialize, Identifier may be GUID-based while
        // NazcaFunctionName preserves the PDK template.
        var nazcaName = component.NazcaFunctionName?.ToLowerInvariant() ?? "";
        return nazcaName.Contains("_gc_") || nazcaName.Contains("edge_coupler") || nazcaName.Contains("grating");
    }

    internal static LaserType GetLaserTypeForWavelength(int wavelengthNm)
    {
        if (wavelengthNm == StandardWaveLengths.RedNM) return LaserType.Red;
        if (wavelengthNm == StandardWaveLengths.GreenNM) return LaserType.Green;
        if (wavelengthNm == StandardWaveLengths.BlueNM) return LaserType.Blue;
        return LaserType.Red;
    }

    private static void MergeFieldResults(
        Dictionary<Guid, Complex> target,
        Dictionary<Guid, Complex> source)
    {
        foreach (var kvp in source)
        {
            if (target.ContainsKey(kvp.Key))
                target[kvp.Key] += kvp.Value;
            else
                target[kvp.Key] = kvp.Value;
        }
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
