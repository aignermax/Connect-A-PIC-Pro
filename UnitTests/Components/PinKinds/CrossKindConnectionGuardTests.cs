using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Core;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Components.PinKinds
{
    /// <summary>
    /// Verifies that optical ↔ electrical connections are rejected on every UI path
    /// (Issue #519). The drag-gesture path is guarded in <c>ConnectionGestureRecognizer</c>;
    /// these tests cover the click-to-connect path and the shared
    /// <see cref="CreateConnectionCommand"/> choke point.
    /// </summary>
    public class CrossKindConnectionGuardTests
    {
        [Fact]
        public void CreateConnectionCommand_CrossKindPins_ExecuteIsNoOp()
        {
            var canvas = new DesignCanvasViewModel();
            var (optical, electrical) = PlaceOpticalAndElectricalComponents(canvas);

            var cmd = new CreateConnectionCommand(canvas, optical, electrical);
            cmd.ArePinKindsCompatible.ShouldBeFalse();
            cmd.Execute();

            canvas.Connections.ShouldBeEmpty();
        }

        [Fact]
        public void ClickToConnect_CrossKindPins_RejectsAndReportsStatus()
        {
            var canvas = new DesignCanvasViewModel();
            var (optical, electrical) = PlaceOpticalAndElectricalComponents(canvas);
            var interaction = new CanvasInteractionViewModel(canvas, new CommandManager());
            string lastStatus = "";
            interaction.UpdateStatus = s => lastStatus = s;
            interaction.CurrentMode = InteractionMode.Connect;

            interaction.PinClicked(optical);
            interaction.PinClicked(electrical);

            canvas.Connections.ShouldBeEmpty();
            lastStatus.ShouldContain("Cannot connect");
            lastStatus.ShouldContain("Optical");
            lastStatus.ShouldContain("Electrical");
        }

        [Fact]
        public void ClickToConnect_SameKindPins_CreatesConnection()
        {
            var canvas = new DesignCanvasViewModel();

            var componentA = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
            componentA.PhysicalX = 0;
            componentA.PhysicalY = 0;
            canvas.AddComponent(componentA, "WaveguideA");

            var componentB = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
            componentB.PhysicalX = 600;
            componentB.PhysicalY = 0;
            canvas.AddComponent(componentB, "WaveguideB");

            var interaction = new CanvasInteractionViewModel(canvas, new CommandManager());
            interaction.CurrentMode = InteractionMode.Connect;

            interaction.PinClicked(componentA.PhysicalPins[1]);
            interaction.PinClicked(componentB.PhysicalPins[0]);

            canvas.Connections.Count.ShouldBe(1);
        }

        /// <summary>
        /// Places two components on the canvas: one with an optical pin and one with an
        /// electrical (bond-pad style) pin, and returns those two pins.
        /// </summary>
        private static (PhysicalPin optical, PhysicalPin electrical) PlaceOpticalAndElectricalComponents(
            DesignCanvasViewModel canvas)
        {
            var opticalComponent = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
            opticalComponent.PhysicalX = 0;
            opticalComponent.PhysicalY = 0;
            canvas.AddComponent(opticalComponent, "Waveguide");

            var padComponent = TestComponentFactory.CreateBasicComponent();
            padComponent.PhysicalX = 600;
            padComponent.PhysicalY = 0;
            padComponent.PhysicalPins.Clear();
            var electricalPin = new PhysicalPin
            {
                Name = "m_pin_top",
                ParentComponent = padComponent,
                OffsetXMicrometers = 0,
                OffsetYMicrometers = 0,
                AngleDegrees = 0,
                LogicalPin = new Pin("m_pin_top", 0, MatterType.Electricity, RectSide.Up)
            };
            padComponent.PhysicalPins.Add(electricalPin);
            canvas.AddComponent(padComponent, "BondPad");

            return (opticalComponent.PhysicalPins[1], electricalPin);
        }
    }
}
