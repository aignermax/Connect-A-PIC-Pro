using System.Collections.Generic;
using CAP.Avalonia.ViewModels.Diagnostics;
using CAP_Core.Analysis;
using CAP_Core.Components.Connections;
using Component = CAP_Core.Components.Core.Component;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels;

public class DesignValidationViewModelPdkCheckTests
{
    private readonly DesignValidationViewModel _vm = new();

    private static Component CreateComponent(string name)
    {
        var comp = TestComponentFactory.CreateStraightWaveGuide();
        comp.HumanReadableName = name;
        return comp;
    }

    [Fact]
    public void RunValidation_ComponentWithLockedPdkSource_ProducesIssue()
    {
        var locked = CreateComponent("LockedComp");
        var pdkSourceByComponent = new Dictionary<Component, string?> { [locked] = "LockedLib" };

        _vm.RunValidation(
            connections: Array.Empty<WaveguideConnection>(),
            allComponents: new[] { locked },
            pdkSourceByComponent: pdkSourceByComponent,
            processAgnosticPdkNames: Array.Empty<string>(),
            enabledPdkNames: Array.Empty<string>());

        _vm.HasIssues.ShouldBeTrue();
        _vm.Issues.ShouldContain(i =>
            i.Type == DesignIssueType.PdkProcessMismatch && i.Description.Contains("LockedComp"));
    }

    [Fact]
    public void RunValidation_ComponentWithEnabledPdkSource_ProducesNoIssue()
    {
        var allowed = CreateComponent("AllowedComp");
        var pdkSourceByComponent = new Dictionary<Component, string?> { [allowed] = "AllowedLib" };

        _vm.RunValidation(
            connections: Array.Empty<WaveguideConnection>(),
            allComponents: new[] { allowed },
            pdkSourceByComponent: pdkSourceByComponent,
            processAgnosticPdkNames: Array.Empty<string>(),
            enabledPdkNames: new[] { "AllowedLib" });

        _vm.HasIssues.ShouldBeFalse();
        _vm.Issues.ShouldNotContain(i => i.Type == DesignIssueType.PdkProcessMismatch);
    }

    [Fact]
    public void RunValidation_ComponentWithProcessAgnosticPdkSource_ProducesNoIssue()
    {
        var tool = CreateComponent("ToolComp");
        var pdkSourceByComponent = new Dictionary<Component, string?> { [tool] = "Analysis Tools" };

        _vm.RunValidation(
            connections: Array.Empty<WaveguideConnection>(),
            allComponents: new[] { tool },
            pdkSourceByComponent: pdkSourceByComponent,
            processAgnosticPdkNames: new[] { "Analysis Tools" },
            enabledPdkNames: Array.Empty<string>());

        _vm.HasIssues.ShouldBeFalse();
    }

    [Fact]
    public void RunValidation_NoPdkDataProvided_SkipsPdkCheckWithoutThrowing()
    {
        var comp = CreateComponent("Comp");

        _vm.RunValidation(
            connections: Array.Empty<WaveguideConnection>(),
            allComponents: new[] { comp });

        _vm.HasIssues.ShouldBeFalse();
    }
}
