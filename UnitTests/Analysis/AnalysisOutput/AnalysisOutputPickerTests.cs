using CAP.Avalonia.Commands;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Panels;
using Shouldly;

namespace UnitTests.Analysis.AnalysisOutput;

/// <summary>
/// Tests for the eyedropper picker mode (#754) in <see cref="CanvasInteractionViewModel"/>:
/// clicking a coupler designates it (switching a still-emitting laser off — explicit user
/// intent), clicking anything else keeps the mode with a hint, and a successful pick
/// returns to Select mode.
/// </summary>
public class AnalysisOutputPickerTests
{
    private static (DesignCanvasViewModel Canvas, CanvasInteractionViewModel Interaction, CommandManager Commands)
        CreateInteraction()
    {
        var canvas = new DesignCanvasViewModel();
        var commands = new CommandManager();
        return (canvas, new CanvasInteractionViewModel(canvas, commands), commands);
    }

    [Fact]
    public void SetPickAnalysisOutputMode_ActivatesPickerMode()
    {
        var (_, interaction, _) = CreateInteraction();

        interaction.SetPickAnalysisOutputModeCommand.Execute(null);

        interaction.CurrentMode.ShouldBe(InteractionMode.PickAnalysisOutput);
    }

    [Fact]
    public void ClickOnOffCoupler_DesignatesIt_AndReturnsToSelectMode()
    {
        var (canvas, interaction, _) = CreateInteraction();
        var coupler = AnalysisOutputTestBed.AddCoupler(canvas, x: 10, y: 10);
        coupler.LaserConfig!.IsEnabled = false;
        interaction.SetPickAnalysisOutputModeCommand.Execute(null);

        interaction.CanvasClicked(15, 15);

        canvas.AnalysisOutput.CouplerId.ShouldBe(coupler.Component.Id);
        interaction.CurrentMode.ShouldBe(InteractionMode.Select);
        coupler.LaserConfig.IsEnabled.ShouldBeFalse("an already-off laser stays off");
    }

    [Fact]
    public void ClickOnEmittingCoupler_SwitchesLaserOff_AndDesignates()
    {
        var (canvas, interaction, _) = CreateInteraction();
        var coupler = AnalysisOutputTestBed.AddCoupler(canvas, x: 10, y: 10);
        coupler.LaserConfig!.IsEnabled.ShouldBeTrue();
        interaction.SetPickAnalysisOutputModeCommand.Execute(null);

        interaction.CanvasClicked(15, 15);

        canvas.AnalysisOutput.CouplerId.ShouldBe(coupler.Component.Id);
        coupler.LaserConfig.IsEnabled.ShouldBeFalse("picking an emitting coupler is explicit user intent to make it an output");
        interaction.CurrentMode.ShouldBe(InteractionMode.Select);
    }

    [Fact]
    public void LaserSwitchOffDuringPick_IsUndoable()
    {
        var (canvas, interaction, commands) = CreateInteraction();
        var coupler = AnalysisOutputTestBed.AddCoupler(canvas, x: 10, y: 10);
        interaction.SetPickAnalysisOutputModeCommand.Execute(null);
        interaction.CanvasClicked(15, 15);

        commands.Undo();

        coupler.LaserConfig!.IsEnabled.ShouldBeTrue("the automatic laser-off must go through the undo stack");
    }

    [Fact]
    public void Undo_AfterPick_RestoresLaserAndRemovesDesignation()
    {
        // Field round 4 review, finding [2]: the pick must be ONE undoable command —
        // undoing only the laser toggle left an orphaned designation that no number of
        // undos could remove, blocking the next Eye/Transient run.
        var (canvas, interaction, commands) = CreateInteraction();
        var coupler = AnalysisOutputTestBed.AddCoupler(canvas, x: 10, y: 10);
        interaction.SetPickAnalysisOutputModeCommand.Execute(null);
        interaction.CanvasClicked(15, 15);

        commands.Undo();

        coupler.LaserConfig!.IsEnabled.ShouldBeTrue("undo must restore the laser");
        canvas.AnalysisOutput.CouplerId.ShouldBeNull("undo must remove the designation too");
    }

    [Fact]
    public void Undo_AfterRepick_RestoresThePreviousDesignation()
    {
        var (canvas, interaction, commands) = CreateInteraction();
        var first = AnalysisOutputTestBed.AddCoupler(canvas, x: 10, y: 10);
        var second = AnalysisOutputTestBed.AddCoupler(canvas, x: 100, y: 10);
        interaction.SetPickAnalysisOutputModeCommand.Execute(null);
        interaction.CanvasClicked(15, 15);
        interaction.SetPickAnalysisOutputModeCommand.Execute(null);
        interaction.CanvasClicked(105, 15);

        commands.Undo();

        canvas.AnalysisOutput.CouplerId.ShouldBe(first.Component.Id,
            "undoing the second pick must restore the first designation");
        second.LaserConfig!.IsEnabled.ShouldBeTrue("the second coupler's laser is restored");
    }

    [Fact]
    public void Redo_AfterUndo_ReappliesLaserOffAndDesignation()
    {
        var (canvas, interaction, commands) = CreateInteraction();
        var coupler = AnalysisOutputTestBed.AddCoupler(canvas, x: 10, y: 10);
        interaction.SetPickAnalysisOutputModeCommand.Execute(null);
        interaction.CanvasClicked(15, 15);
        commands.Undo();

        commands.Redo();

        coupler.LaserConfig!.IsEnabled.ShouldBeFalse();
        canvas.AnalysisOutput.CouplerId.ShouldBe(coupler.Component.Id);
    }

    [Fact]
    public void ClickOnNonCoupler_KeepsPickerMode_AndShowsHint()
    {
        var (canvas, interaction, _) = CreateInteraction();
        AnalysisOutputTestBed.AddPlainComponent(canvas, x: 10, y: 10);
        string? status = null;
        interaction.UpdateStatus = s => status = s;
        interaction.SetPickAnalysisOutputModeCommand.Execute(null);

        interaction.CanvasClicked(15, 15);

        canvas.AnalysisOutput.CouplerId.ShouldBeNull();
        interaction.CurrentMode.ShouldBe(InteractionMode.PickAnalysisOutput);
        status.ShouldBe(LocalizationService.Instance.Translate("Analysis.Output.PickNotACoupler"));
    }

    [Fact]
    public void ClickOnEmptyCanvas_KeepsPickerMode()
    {
        var (canvas, interaction, _) = CreateInteraction();
        interaction.SetPickAnalysisOutputModeCommand.Execute(null);

        interaction.CanvasClicked(500, 500);

        canvas.AnalysisOutput.CouplerId.ShouldBeNull();
        interaction.CurrentMode.ShouldBe(InteractionMode.PickAnalysisOutput);
    }

    [Fact]
    public void SwitchingMode_CancelsThePicker_WithoutDesignating()
    {
        var (canvas, interaction, _) = CreateInteraction();
        AnalysisOutputTestBed.AddCoupler(canvas, x: 10, y: 10);
        interaction.SetPickAnalysisOutputModeCommand.Execute(null);

        interaction.SetSelectModeCommand.Execute(null);

        interaction.CurrentMode.ShouldBe(InteractionMode.Select);
        canvas.AnalysisOutput.CouplerId.ShouldBeNull();
    }

    [Fact]
    public void PickingAnotherCoupler_ReplacesTheDesignation()
    {
        var (canvas, interaction, _) = CreateInteraction();
        var first = AnalysisOutputTestBed.AddCoupler(canvas, x: 10, y: 10);
        var second = AnalysisOutputTestBed.AddCoupler(canvas, x: 100, y: 10);
        interaction.SetPickAnalysisOutputModeCommand.Execute(null);
        interaction.CanvasClicked(15, 15);
        interaction.SetPickAnalysisOutputModeCommand.Execute(null);

        interaction.CanvasClicked(105, 15);

        canvas.AnalysisOutput.CouplerId.ShouldBe(second.Component.Id);
        canvas.AnalysisOutput.CouplerId.ShouldNotBe(first.Component.Id);
    }
}
