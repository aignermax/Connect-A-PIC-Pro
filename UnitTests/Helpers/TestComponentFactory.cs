using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Tiles;
using System.Numerics;
using CAP_Core.LightCalculation;

namespace UnitTests
{
    /// <summary>
    /// Factory for creating test components without external dependencies.
    /// </summary>
    public class TestComponentFactory
    {
        public static Component CreateStraightWaveGuide()
        {
            int widthInTiles = 1;
            int heightInTiles = 1;

            Part[,] parts = new Part[widthInTiles, heightInTiles];

            parts[0, 0] = new Part(new List<Pin>() {
                new ("west0",0, MatterType.Light, RectSide.Left),
                new ("east0",1, MatterType.Light, RectSide.Right)
            });


            var leftIn = parts[0, 0].GetPinAt(RectSide.Left).IDInFlow;
            var rightOut = parts[0, 0].GetPinAt(RectSide.Right).IDOutFlow;
            var rightIn = parts[0, 0].GetPinAt(RectSide.Right).IDInFlow;
            var leftOut = parts[0, 0].GetPinAt(RectSide.Left).IDOutFlow;

            var allPins = Component.GetAllPins(parts).SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();
            var matrixRed = new SMatrix(allPins, new());
            // set the connections
            matrixRed.SetValues(new(){
                { (leftIn, rightOut), 1 },
                { (rightIn, leftOut), 1 },
            });
            var connections = new Dictionary<int, SMatrix>
            {
                { StandardWaveLengths.RedNM, matrixRed},
                { StandardWaveLengths.GreenNM, matrixRed},
                { StandardWaveLengths.BlueNM, matrixRed},
            };

            return new Component(connections, new(), "placeCell_StraightWG", "", parts, 0, "Straight", DiscreteRotation.R0);
        }

        public static Component CreateDirectionalCoupler()
        {
            int widthInTiles = 2;
            int heightInTiles = 2;

            Part[,] parts = new Part[widthInTiles, heightInTiles];


            parts[0, 0] = new Part(new List<Pin>() { new Pin("west0", 0, MatterType.Light, RectSide.Left) });
            parts[1, 0] = new Part(new List<Pin>() { new Pin("east0", 1, MatterType.Light, RectSide.Right) });
            parts[1, 1] = new Part(new List<Pin>() { new Pin("east1", 2, MatterType.Light, RectSide.Right) });
            parts[0, 1] = new Part(new List<Pin>() { new Pin("west1", 3, MatterType.Light, RectSide.Left) });

            // setting up the connections
            var leftUpIn = parts[0, 0].GetPinAt(RectSide.Left).IDInFlow;
            var leftUpOut = parts[0, 0].GetPinAt(RectSide.Left).IDOutFlow;
            var leftDownIn = parts[0, 1].GetPinAt(RectSide.Left).IDInFlow;
            var leftDownOut = parts[0, 1].GetPinAt(RectSide.Left).IDOutFlow;
            var rightUpIn = parts[1, 0].GetPinAt(RectSide.Right).IDInFlow;
            var rightUpOut = parts[1, 0].GetPinAt(RectSide.Right).IDOutFlow;
            var rightDownIn = parts[1, 1].GetPinAt(RectSide.Right).IDInFlow;
            var rightDownOut = parts[1, 1].GetPinAt(RectSide.Right).IDOutFlow;

            var allPins = Component.GetAllPins(parts).SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();
            var matrixRed = new SMatrix(allPins, new());
            // set the connections
            matrixRed.SetValues(new(){
                { (leftUpIn, rightUpOut), Math.Sqrt(0.5f) },
                { (leftUpIn, rightDownOut), Math.Sqrt(0.5f) },
                { (leftDownIn, rightUpOut), Math.Sqrt(0.5f) },
                { (leftDownIn, rightDownOut), Math.Sqrt(0.5f) },
                { (rightUpIn, leftUpOut), Math.Sqrt(0.5f) },
                { (rightUpIn, leftDownOut), Math.Sqrt(0.5f) },
                { (rightDownIn, leftUpOut), Math.Sqrt(0.5f) },
                { (rightDownIn, leftDownOut), Math.Sqrt(0.5f) },

            });
            var connections = new Dictionary<int, SMatrix>
            {
                {StandardWaveLengths.RedNM, matrixRed},
                {StandardWaveLengths.GreenNM, matrixRed},
                {StandardWaveLengths.BlueNM, matrixRed},
            };
            return new Component(connections, new(), "placeCell_DirectionalCoupler", "", parts, 0, "DirectionalCoupler", DiscreteRotation.R0);
        }

        /// <summary>
        /// Creates a straight waveguide component with physical pins for testing.
        /// </summary>
        public static Component CreateStraightWaveGuideWithPhysicalPins()
        {
            var component = CreateStraightWaveGuide();

            // Get logical pins
            var logicalPin1 = component.Parts[0, 0].GetPinAt(RectSide.Left);
            var logicalPin2 = component.Parts[0, 0].GetPinAt(RectSide.Right);

            // Add physical pins with logical pin references
            var inputPin = new PhysicalPin
            {
                Name = "in",
                ParentComponent = component,
                OffsetXMicrometers = 0,
                OffsetYMicrometers = 125,
                AngleDegrees = 180,
                LogicalPin = logicalPin1
            };

            var outputPin = new PhysicalPin
            {
                Name = "out",
                ParentComponent = component,
                OffsetXMicrometers = 250,
                OffsetYMicrometers = 125,
                AngleDegrees = 0,
                LogicalPin = logicalPin2
            };

            component.PhysicalPins.Add(inputPin);
            component.PhysicalPins.Add(outputPin);

            return component;
        }

        /// <summary>
        /// Creates a 250×250 µm component with four physical pins (two per side), mimicking
        /// a directional coupler: west0/east0 in the upper row, west1/east1 in the lower row.
        /// </summary>
        public static Component CreateFourPinCouplerWithPhysicalPins(string idSuffix)
        {
            var component = CreateStraightWaveGuide();
            component.Identifier = $"coupler_{idSuffix}";
            component.WidthMicrometers = 250;
            component.HeightMicrometers = 250;

            AddPhysicalPin(component, "west0", 0, 80, 180);
            AddPhysicalPin(component, "west1", 0, 180, 180);
            AddPhysicalPin(component, "east0", 250, 80, 0);
            AddPhysicalPin(component, "east1", 250, 180, 0);
            return component;
        }

        /// <summary>
        /// Creates a flat 4-pin coupler with the Cornerstone SiN "Coupler Straight" footprint:
        /// 20 × 2.636 µm with two pins per side only 1.436 µm apart vertically
        /// (west0/east0 upper row at y = 0.6, west1/east1 lower row at y = 2.036).
        /// </summary>
        public static Component CreateFlatCouplerWithPhysicalPins(string idSuffix)
        {
            var component = CreateStraightWaveGuide();
            component.Identifier = $"flat_coupler_{idSuffix}";
            component.WidthMicrometers = 20;
            component.HeightMicrometers = 2.636;

            AddPhysicalPin(component, "west0", 0, 0.6, 180);
            AddPhysicalPin(component, "west1", 0, 2.036, 180);
            AddPhysicalPin(component, "east0", 20, 0.6, 0);
            AddPhysicalPin(component, "east1", 20, 2.036, 0);
            return component;
        }

        /// <summary>
        /// Creates a flat 2-pin straight with the Cornerstone SiN "Straight" footprint:
        /// 10 × 1.2 µm with pins at mid-height (y = 0.6).
        /// </summary>
        public static Component CreateFlatStraightWithPhysicalPins(string idSuffix)
        {
            var component = CreateStraightWaveGuide();
            component.Identifier = $"flat_straight_{idSuffix}";
            component.WidthMicrometers = 10;
            component.HeightMicrometers = 1.2;

            AddPhysicalPin(component, "in", 0, 0.6, 180);
            AddPhysicalPin(component, "out", 10, 0.6, 0);
            return component;
        }

        /// <summary>Adds a physical pin at the given component-relative offset and angle.</summary>
        private static void AddPhysicalPin(
            Component component, string name, double offsetX, double offsetY, double angleDegrees)
        {
            component.PhysicalPins.Add(new PhysicalPin
            {
                Name = name,
                ParentComponent = component,
                OffsetXMicrometers = offsetX,
                OffsetYMicrometers = offsetY,
                AngleDegrees = angleDegrees,
            });
        }

        /// <summary>
        /// Creates a pinless rectangular component for routing-obstacle tests. Register any
        /// pins created via <see cref="CreateRoutingPin"/> in <see cref="Component.PhysicalPins"/>
        /// BEFORE the pathfinding grid is (re)built, so the grid carves the persistent pin
        /// corridors exactly like for every real component.
        /// </summary>
        public static Component CreatePinlessComponent(
            double x, double y, double width = 50, double height = 50)
        {
            var parts = new Part[1, 1];
            parts[0, 0] = new Part(new List<Pin>());
            return new Component(
                new Dictionary<int, SMatrix>(), new(), "test", "",
                parts, 0, $"TestComponent_{x}_{y}", DiscreteRotation.R0)
            {
                WidthMicrometers = width,
                HeightMicrometers = height,
                PhysicalX = x,
                PhysicalY = y,
            };
        }

        /// <summary>
        /// Creates a physical pin at a component-relative offset and heading. The pin is NOT
        /// added to the parent's pin list — callers decide whether it participates in the
        /// grid's pin-corridor carving.
        /// </summary>
        public static PhysicalPin CreateRoutingPin(
            Component parent, double offsetX, double offsetY, double angleDegrees)
        {
            return new PhysicalPin
            {
                Name = $"pin_{offsetX}_{offsetY}_{angleDegrees}",
                OffsetXMicrometers = offsetX,
                OffsetYMicrometers = offsetY,
                AngleDegrees = angleDegrees,
                ParentComponent = parent,
            };
        }

        /// <summary>
        /// Creates a basic component for testing (uses CreateStraightWaveGuide).
        /// </summary>
        public static Component CreateBasicComponent()
        {
            var component = CreateStraightWaveGuide();
            component.PhysicalX = 0;
            component.PhysicalY = 0;
            component.WidthMicrometers = 250;
            component.HeightMicrometers = 250;
            return component;
        }

        /// <summary>
        /// Creates a simple two-port component with physical pins for testing ComponentGroups.
        /// </summary>
        public static Component CreateSimpleTwoPortComponent()
        {
            var component = CreateStraightWaveGuide();
            component.WidthMicrometers = 10;
            component.HeightMicrometers = 1;

            // Add physical pins with logical pin references
            var logicalPin1 = component.Parts[0, 0].GetPinAt(RectSide.Left);
            var logicalPin2 = component.Parts[0, 0].GetPinAt(RectSide.Right);

            var physicalPin1 = new PhysicalPin
            {
                Name = "in",
                ParentComponent = component,
                OffsetXMicrometers = 0,
                OffsetYMicrometers = 0.5,
                AngleDegrees = 180,
                LogicalPin = logicalPin1
            };

            var physicalPin2 = new PhysicalPin
            {
                Name = "out",
                ParentComponent = component,
                OffsetXMicrometers = 10,
                OffsetYMicrometers = 0.5,
                AngleDegrees = 0,
                LogicalPin = logicalPin2
            };

            component.PhysicalPins.Clear();
            component.PhysicalPins.Add(physicalPin1);
            component.PhysicalPins.Add(physicalPin2);

            return component;
        }

        /// <summary>
        /// Creates a 2-port phase-shifter component with physical pins.
        /// <paramref name="forward"/> defines the left→right transfer (emitted as
        /// <c>s21</c> in the Verilog-A export). <paramref name="backward"/>, if
        /// provided, defines the right→left transfer (<c>s12</c>); otherwise the
        /// component is reciprocal and both directions share <paramref name="forward"/>.
        /// The asymmetric form lets tests prove that ExtractSParameters registers
        /// each direction independently rather than aliasing them.
        /// </summary>
        public static Component CreatePhaseShifterWithPhysicalPins(Complex forward, Complex? backward = null)
        {
            var backwardValue = backward ?? forward;

            Part[,] parts = new Part[1, 1];
            parts[0, 0] = new Part(new List<Pin>() {
                new ("west0", 0, MatterType.Light, RectSide.Left),
                new ("east0", 1, MatterType.Light, RectSide.Right)
            });

            var leftIn = parts[0, 0].GetPinAt(RectSide.Left).IDInFlow;
            var rightOut = parts[0, 0].GetPinAt(RectSide.Right).IDOutFlow;
            var rightIn = parts[0, 0].GetPinAt(RectSide.Right).IDInFlow;
            var leftOut = parts[0, 0].GetPinAt(RectSide.Left).IDOutFlow;

            var allPins = Component.GetAllPins(parts).SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();
            var matrix = new SMatrix(allPins, new());
            matrix.SetValues(new() {
                { (leftIn, rightOut), forward },       // forward  = S21
                { (rightIn, leftOut), backwardValue }, // backward = S12
            });

            var connections = new Dictionary<int, SMatrix>
            {
                { StandardWaveLengths.RedNM, matrix },
            };

            var comp = new Component(connections, new(), "phase_shifter", "", parts, 0, "PhaseShifter", DiscreteRotation.R0);

            var logicalPin1 = comp.Parts[0, 0].GetPinAt(RectSide.Left);
            var logicalPin2 = comp.Parts[0, 0].GetPinAt(RectSide.Right);

            comp.PhysicalPins.Add(new PhysicalPin
            {
                Name = "in",
                ParentComponent = comp,
                LogicalPin = logicalPin1,
            });
            comp.PhysicalPins.Add(new PhysicalPin
            {
                Name = "out",
                ParentComponent = comp,
                LogicalPin = logicalPin2,
            });

            return comp;
        }

        /// <summary>
        /// Creates a WaveguideConnection between two components for testing.
        /// </summary>
        public static WaveguideConnection CreateConnection(Component startComponent, Component endComponent)
        {
            // Ensure components have physical pins
            if (startComponent.PhysicalPins.Count == 0)
            {
                var startPin = new PhysicalPin
                {
                    Name = "out",
                    ParentComponent = startComponent,
                    OffsetXMicrometers = 250,
                    OffsetYMicrometers = 125,
                    AngleDegrees = 0
                };
                startComponent.PhysicalPins.Add(startPin);
            }

            if (endComponent.PhysicalPins.Count == 0)
            {
                var endPin = new PhysicalPin
                {
                    Name = "in",
                    ParentComponent = endComponent,
                    OffsetXMicrometers = 0,
                    OffsetYMicrometers = 125,
                    AngleDegrees = 180
                };
                endComponent.PhysicalPins.Add(endPin);
            }

            var connection = new WaveguideConnection
            {
                StartPin = startComponent.PhysicalPins[0],
                EndPin = endComponent.PhysicalPins[0]
            };

            return connection;
        }

        /// <summary>
        /// Creates a <see cref="ComponentViewModel"/> for testing with a configurable
        /// Nazca function name and optional dimensions.
        /// </summary>
        public static ComponentViewModel CreateComponentViewModel(
            string? nazcaFunctionName = "demo.shallow.strt",
            double widthMicrometers = 10,
            double heightMicrometers = 10)
        {
            var component = CreateStraightWaveGuide();
            component.NazcaFunctionName = nazcaFunctionName ?? "";
            component.WidthMicrometers = widthMicrometers;
            component.HeightMicrometers = heightMicrometers;
            return new ComponentViewModel(component);
        }

        /// <summary>
        /// Creates a ComponentGroup for testing with optional child components.
        /// </summary>
        /// <param name="groupName">Name of the group.</param>
        /// <param name="addChildren">If true, adds two basic components to the group.</param>
        public static ComponentGroup CreateComponentGroup(string groupName = "TestGroup", bool addChildren = false)
        {
            var group = new ComponentGroup(groupName)
            {
                PhysicalX = 0,
                PhysicalY = 0,
                Description = "Test component group"
            };

            if (addChildren)
            {
                var child1 = CreateBasicComponent();
                child1.PhysicalX = 100;
                child1.PhysicalY = 100;
                group.AddChild(child1);

                var child2 = CreateBasicComponent();
                child2.PhysicalX = 400;
                child2.PhysicalY = 100;
                group.AddChild(child2);
            }

            return group;
        }
    }
}
