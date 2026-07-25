using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.ComponentSettings;
using CAP.Avalonia.Views;
using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Persistence.PIR;
using Moq;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI;

/// <summary>
/// Regression guard: the SolverStatus bar in the component-settings dialog
/// must render long messages (e.g. the Docker install hint) fully inside the
/// window. The original layout used a horizontal StackPanel, which measures
/// its children with infinite width — TextWrapping never engaged and the
/// message was cut off at the window edge.
/// </summary>
// Renders the real dialog window — CI-only (local runners exclude Category=Slow).
[Trait("Category", "Slow")]
public class ComponentSettingsDialogSolverStatusTests
{
    private const string DockerHint =
        "Docker is not installed (or not on PATH). FDTD needs Docker Desktop — " +
        "install it from https://www.docker.com/products/docker-desktop/, then retry.";

    [AvaloniaFact]
    public async Task SolverStatus_LongDockerMessage_WrapsInsideWindowBounds()
    {
        var service = new Mock<IFdtdSMatrixService>();
        service.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdAvailability.Unavailable(DockerHint));

        var vm = new ComponentSettingsDialogViewModel(
            Mock.Of<IFileDialogService>(),
            fdtdService: service.Object,
            fdtdRequestFactory: (_, _) => Task.FromResult<FdtdSMatrixRequest?>(null));
        vm.Configure("comp", "comp", "Comp", new Dictionary<string, ComponentSMatrixData>(),
            liveComponent: TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins());

        var window = new ComponentSettingsDialog { DataContext = vm };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            await vm.RecalculateSMatrixCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            vm.SolverStatus.ShouldBe(DockerHint);

            var statusText = window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Single(t => t.Text == DockerHint);

            // With the broken StackPanel layout the TextBlock measured ~1200px
            // wide and ran past the 620px window; wrapped it stays inside.
            var rightEdgeInWindow = statusText
                .TranslatePoint(new Point(statusText.Bounds.Width, 0), window)!
                .Value.X;
            rightEdgeInWindow.ShouldBeLessThanOrEqualTo(window.ClientSize.Width);

            // Wrapping must actually engage: the message needs more than one line.
            var oneLineHeight = statusText.FontSize * 2;
            statusText.Bounds.Height.ShouldBeGreaterThan(oneLineHeight,
                "the long Docker hint should wrap onto multiple lines");
        }
        finally
        {
            window.Close();
        }
    }
}
