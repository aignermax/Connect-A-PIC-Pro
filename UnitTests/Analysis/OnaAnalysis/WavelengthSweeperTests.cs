using CAP_Core.Analysis.OnaAnalysis;
using CAP_Core.ExternalPorts;
using CAP_Core.Grid;
using CAP_Core.LightCalculation;
using Moq;
using Shouldly;
using System.Collections.Concurrent;
using System.Numerics;
using Xunit;

namespace UnitTests.Analysis.OnaAnalysis;

public class WavelengthSweeperTests
{
    // ── helpers ────────────────────────────────────────────────────────────────

    private static Mock<ISystemMatrixBuilder> CreateMockBuilder(Dictionary<Guid, Complex>? fields = null)
    {
        var pin1 = Guid.NewGuid();
        var pin2 = Guid.NewGuid();
        fields ??= new Dictionary<Guid, Complex>
        {
            { pin1, new Complex(0.7, 0) },
            { pin2, new Complex(0.3, 0) },
        };

        var pins = fields.Keys.ToList();
        var sliders = new List<(Guid, double)>();
        var matrix = new SMatrix(pins, sliders);

        var mockBuilder = new Mock<ISystemMatrixBuilder>();
        mockBuilder.Setup(b => b.GetSystemSMatrix(It.IsAny<int>())).Returns(matrix);
        return mockBuilder;
    }

    private static Mock<IExternalPortManager> CreateMockPortManager(double inputPower = 1.0)
    {
        var inputGuid = Guid.NewGuid();
        var input = new ExternalInput("test-input", LaserType.Red, 0, new Complex(inputPower, 0));
        var usedInput = new UsedInput(input, inputGuid);

        var mockPorts = new Mock<IExternalPortManager>();
        mockPorts.Setup(p => p.GetAllExternalInputs())
            .Returns(new ConcurrentBag<ExternalInput> { input });
        mockPorts.Setup(p => p.GetUsedExternalInputs())
            .Returns(new ConcurrentBag<UsedInput> { usedInput });
        return mockPorts;
    }

    private static GridManager CreateMinimalGridManager()
    {
        return new GridManager(4, 4);
    }

    // ── constructor ────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullBuilder_ThrowsArgumentNullException()
    {
        var mockPorts = CreateMockPortManager();
        Should.Throw<ArgumentNullException>(() => new WavelengthSweeper(null!, mockPorts.Object));
    }

    [Fact]
    public void Constructor_NullPortManager_ThrowsArgumentNullException()
    {
        var mockBuilder = CreateMockBuilder();
        Should.Throw<ArgumentNullException>(() => new WavelengthSweeper(mockBuilder.Object, null!));
    }

    // ── RunSweepAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RunSweepAsync_NullConfiguration_ThrowsArgumentNullException()
    {
        var sweeper = new WavelengthSweeper(CreateMockBuilder().Object, CreateMockPortManager().Object);
        var grid = CreateMinimalGridManager();

        await Should.ThrowAsync<ArgumentNullException>(
            () => sweeper.RunSweepAsync(null!, grid));
    }

    [Fact]
    public async Task RunSweepAsync_NullGridManager_ThrowsArgumentNullException()
    {
        var sweeper = new WavelengthSweeper(CreateMockBuilder().Object, CreateMockPortManager().Object);
        var config = new WavelengthSweepConfiguration(1500, 1600, 5);

        await Should.ThrowAsync<ArgumentNullException>(
            () => sweeper.RunSweepAsync(config, null!));
    }

    [Fact]
    public async Task RunSweepAsync_ReturnsCorrectNumberOfDataPoints()
    {
        var sweeper = new WavelengthSweeper(CreateMockBuilder().Object, CreateMockPortManager().Object);
        var config = new WavelengthSweepConfiguration(1500, 1600, 11);
        var grid = CreateMinimalGridManager();

        var result = await sweeper.RunSweepAsync(config, grid);

        result.DataPoints.Count.ShouldBe(11);
    }

    [Fact]
    public async Task RunSweepAsync_WavelengthsMatchConfiguration()
    {
        var sweeper = new WavelengthSweeper(CreateMockBuilder().Object, CreateMockPortManager().Object);
        var config = new WavelengthSweepConfiguration(1500, 1600, 3);
        var grid = CreateMinimalGridManager();

        var result = await sweeper.RunSweepAsync(config, grid);

        result.DataPoints[0].WavelengthNm.ShouldBe(1500);
        result.DataPoints[1].WavelengthNm.ShouldBe(1550);
        result.DataPoints[2].WavelengthNm.ShouldBe(1600);
    }

    [Fact]
    public async Task RunSweepAsync_QueriesBuilderAtEachWavelength()
    {
        var mockBuilder = CreateMockBuilder();
        var sweeper = new WavelengthSweeper(mockBuilder.Object, CreateMockPortManager().Object);
        var config = new WavelengthSweepConfiguration(1500, 1600, 3);
        var grid = CreateMinimalGridManager();

        await sweeper.RunSweepAsync(config, grid);

        mockBuilder.Verify(b => b.GetSystemSMatrix(1500), Times.Once);
        mockBuilder.Verify(b => b.GetSystemSMatrix(1550), Times.Once);
        mockBuilder.Verify(b => b.GetSystemSMatrix(1600), Times.Once);
    }

    [Fact]
    public async Task RunSweepAsync_CancellationToken_StopsEarly()
    {
        int callCount = 0;
        var pin = Guid.NewGuid();
        var sliders = new List<(Guid, double)>();
        var matrix = new SMatrix(new List<Guid> { pin }, sliders);

        var mockBuilder = new Mock<ISystemMatrixBuilder>();
        mockBuilder.Setup(b => b.GetSystemSMatrix(It.IsAny<int>()))
            .Returns(() =>
            {
                callCount++;
                return matrix;
            });

        var cts = new CancellationTokenSource();
        var sweeper = new WavelengthSweeper(mockBuilder.Object, CreateMockPortManager().Object);
        var config = new WavelengthSweepConfiguration(1500, 1600, 50);
        var grid = CreateMinimalGridManager();

        // Cancel after 3 calls
        mockBuilder.Setup(b => b.GetSystemSMatrix(It.IsAny<int>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount >= 3) cts.Cancel();
                return matrix;
            });

        await Should.ThrowAsync<OperationCanceledException>(
            () => sweeper.RunSweepAsync(config, grid, cts.Token));
    }

    [Fact]
    public async Task RunSweepAsync_GdsFactoryComponent_WarningNamesTheGdsFactoryFunction()
    {
        // gdsfactory-backend components carry a synthesized "nazca_<name>" placeholder as
        // NazcaFunctionName; coverage warnings used it as the label, reporting e.g.
        // 'nazca_coupler_straight' for a cspdk.sin300.coupler_straight (#712). The
        // warning must name the real gdsfactory factory instead.
        var grid = CreateMinimalGridManager();
        var component = TestComponentFactory.CreateStraightWaveGuide(); // map: 980/1310/1550
        component.GdsFactoryFunction = "cspdk.sin300.coupler_straight";
        grid.ComponentMover.PlaceComponent(0, 0, component);

        var sweeper = new WavelengthSweeper(CreateMockBuilder().Object, CreateMockPortManager().Object);
        var config = new WavelengthSweepConfiguration(1500, 1600, 3); // extrapolates above 1550

        var result = await sweeper.RunSweepAsync(config, grid);

        result.Warnings.ShouldContain(w => w.Contains("cspdk.sin300.coupler_straight"));
        result.Warnings.ShouldAllBe(w => !w.Contains(component.NazcaFunctionName));
    }
}
