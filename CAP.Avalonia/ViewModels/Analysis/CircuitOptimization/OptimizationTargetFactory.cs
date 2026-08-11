using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Core;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;

namespace CAP.Avalonia.ViewModels.Analysis.CircuitOptimization;

/// <summary>
/// Builds the selectable optimization targets from the couplers on the canvas:
/// one combined "total power at all outputs" option (listen-only coupler pins)
/// followed by one option per individual coupler light pin.
/// </summary>
public static class OptimizationTargetFactory
{
    /// <summary>Creates the target options for the given canvas components.</summary>
    public static IReadOnlyList<OptimizationTargetOption> Build(
        IEnumerable<ComponentViewModel> components)
    {
        var outputPins = new List<Guid>();
        var singleOptions = new List<OptimizationTargetOption>();
        foreach (var componentVm in components)
        {
            if (!componentVm.IsLightSource) continue;
            CollectCouplerTargets(componentVm, outputPins, singleOptions);
        }

        var targets = new List<OptimizationTargetOption>();
        if (outputPins.Count > 0)
        {
            targets.Add(new OptimizationTargetOption(
                LocalizationService.Instance.Translate("Optimize.TargetTotal"), outputPins));
        }
        targets.AddRange(singleOptions);
        return targets;
    }

    private static void CollectCouplerTargets(
        ComponentViewModel componentVm,
        List<Guid> outputPins,
        List<OptimizationTargetOption> singleOptions)
    {
        foreach (var pin in componentVm.Component.PhysicalPins)
        {
            if (pin.LogicalPin?.MatterType != MatterType.Light) continue;

            var pinId = pin.LogicalPin.IDInFlow;
            singleOptions.Add(new OptimizationTargetOption(
                $"{componentVm.Name}.{pin.Name}", new[] { pinId }));

            // Only listen-only couplers (laser off, #690) count as circuit outputs.
            if (componentVm.IsLaserOff)
                outputPins.Add(pinId);
        }
    }
}
