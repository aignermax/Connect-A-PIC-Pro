using CAP.Avalonia.ViewModels.Analysis;
using CAP.Avalonia.ViewModels.Analysis.AnalysisOutput;
using CAP.Avalonia.ViewModels.Analysis.EyeDiagram;
using CAP.Avalonia.ViewModels.Analysis.MonteCarloAnalysis;
using CAP.Avalonia.ViewModels.Analysis.WavelengthSpectrum;
using CAP.Avalonia.ViewModels.Panels;
using Shouldly;

namespace UnitTests.ViewModels.Panels;

public class AnalysisDockViewModelTests
{
    private static AnalysisDockViewModel Make() =>
        new(new TimeDomainViewModel(), new EyeDiagramViewModel(),
            new WavelengthSpectrumViewModel(), new AnalysisOutputPanelViewModel(),
            new MonteCarloViewModel());

    [Fact]
    public void StartsCollapsed_OnTransientTab()
    {
        var vm = Make();
        vm.IsVisible.ShouldBeFalse();
        vm.SelectedTabIndex.ShouldBe(0);
    }

    [Fact]
    public void Toggle_FlipsVisibility()
    {
        var vm = Make();
        vm.ToggleCommand.Execute(null);
        vm.IsVisible.ShouldBeTrue();
    }

    [Fact]
    public void OpenTransient_ShowsDockOnTransientTab()
    {
        var vm = Make();
        vm.SelectedTabIndex = 1;
        vm.OpenTransient();
        vm.IsVisible.ShouldBeTrue();
        vm.SelectedTabIndex.ShouldBe(0);
    }

    [Fact]
    public void SetDockHeight_ClampsToMinAndMax()
    {
        var vm = Make();
        vm.SetDockHeight(10_000);
        vm.DockHeight.ShouldBe(AnalysisDockViewModel.MaxDockHeight);
        vm.SetDockHeight(1);
        vm.DockHeight.ShouldBe(AnalysisDockViewModel.MinDockHeight);
        vm.SetDockHeight(300);
        vm.DockHeight.ShouldBe(300);
    }
}
