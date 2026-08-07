using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Analysis.CircuitOptimization;

public class ApplyOptimizationVariantCommandTests
{
    private static ComponentViewModel CreateComponentVm(double sliderValue)
    {
        // The Component constructor resets sliders to their midpoint,
        // so the start value must be set explicitly after construction.
        var component = TestComponentHelper.CreateComponentWithSlider(0, 1, sliderValue);
        component.GetSlider(0)!.Value = sliderValue;
        return new ComponentViewModel(component);
    }

    [Fact]
    public void Execute_AppliesAllSliderValues()
    {
        var first = CreateComponentVm(0.2);
        var second = CreateComponentVm(0.4);
        var command = new ApplyOptimizationVariantCommand(
            new[] { (first, 0.7), (second, 0.9) }, "variant #1");

        command.Execute();

        first.SliderValue.ShouldBe(0.7);
        second.SliderValue.ShouldBe(0.9);
    }

    [Fact]
    public void Undo_RestoresPreviousSliderValues()
    {
        var first = CreateComponentVm(0.2);
        var second = CreateComponentVm(0.4);
        var command = new ApplyOptimizationVariantCommand(
            new[] { (first, 0.7), (second, 0.9) }, "variant #1");

        command.Execute();
        command.Undo();

        first.SliderValue.ShouldBe(0.2);
        second.SliderValue.ShouldBe(0.4);
    }

    [Fact]
    public void ExecutedThroughCommandManager_IsUndoable()
    {
        var component = CreateComponentVm(0.25);
        var manager = new CommandManager();

        manager.ExecuteCommand(new ApplyOptimizationVariantCommand(
            new[] { (component, 0.8) }, "variant #2"));
        component.SliderValue.ShouldBe(0.8);

        manager.Undo();
        component.SliderValue.ShouldBe(0.25);
    }

    [Fact]
    public void Description_NamesTheVariant()
    {
        var command = new ApplyOptimizationVariantCommand(
            new[] { (CreateComponentVm(0.5), 0.6) }, "variant #3");

        command.Description.ShouldContain("variant #3");
    }
}
