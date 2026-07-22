using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Diagnostics;
using CAP_Core.LightCalculation;
using Shouldly;
using Xunit;

namespace UnitTests.LightCalculation;

/// <summary>
/// Integration tests for S-Matrix performance diagnostics.
/// Tests the flow from Core (SMatrixStatisticsAnalyzer) → ViewModel (SMatrixPerformanceViewModel).
/// </summary>
public class SMatrixPerformanceIntegrationTests
{
    /// <summary>
    /// The ViewModel's StatusText/StorageType/MemorySavings strings are localized via
    /// LocalizationService.Instance, so pin English to keep these assertions culture-independent
    /// regardless of the CI/dev OS language.
    /// </summary>
    public SMatrixPerformanceIntegrationTests()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
    }

    [Fact]
    public void ViewModel_DisplaysStatisticsFromAnalyzer()
    {
        // Arrange
        var pins = CreatePinList(20); // 20x20 matrix
        var matrix = new SMatrix(pins, new List<(Guid, double)>());

        // Add sparse connections (only 10 out of 400 elements)
        var transfers = new Dictionary<(Guid, Guid), System.Numerics.Complex>();
        for (int i = 0; i < 10; i++)
        {
            transfers[(pins[i], pins[(i + 1) % 20])] = new System.Numerics.Complex(0.8, 0);
        }
        matrix.SetValues(transfers);

        var viewModel = new SMatrixPerformanceViewModel();

        // Act
        viewModel.AnalyzeMatrix(matrix);

        // Assert
        viewModel.HasAnalysis.ShouldBeTrue();
        viewModel.MatrixSizeText.ShouldBe("20 × 20");
        viewModel.TotalElementsText.ShouldContain("400");
        viewModel.NonZeroElementsText.ShouldContain("10");
        // SparsityText contains percentage - check it's high (>90%)
        double sparsity = double.Parse(viewModel.SparsityText.Replace("%", "").Replace(",", "."),
            System.Globalization.CultureInfo.InvariantCulture);
        sparsity.ShouldBeGreaterThan(90.0);
        viewModel.StorageTypeText.ShouldContain("Sparse");
        viewModel.StatusText.ShouldBe("Analysis complete");
    }

    [Fact]
    public void ViewModel_HandlesNullMatrix()
    {
        // Arrange
        var viewModel = new SMatrixPerformanceViewModel();

        // Act
        viewModel.AnalyzeMatrix(null);

        // Assert
        viewModel.HasAnalysis.ShouldBeFalse();
        viewModel.MatrixSizeText.ShouldBe("-");
        viewModel.StatusText.ShouldBe("No S-Matrix available");
    }

    [Fact]
    public void ViewModel_ShowsMemorySavings()
    {
        // Arrange
        var pins = CreatePinList(100); // Large matrix
        var matrix = new SMatrix(pins, new List<(Guid, double)>());

        // Very sparse - only 50 connections out of 10,000
        var transfers = new Dictionary<(Guid, Guid), System.Numerics.Complex>();
        for (int i = 0; i < 50; i++)
        {
            transfers[(pins[i], pins[(i + 1) % 100])] = new System.Numerics.Complex(0.5, 0);
        }
        matrix.SetValues(transfers);

        var viewModel = new SMatrixPerformanceViewModel();

        // Act
        viewModel.AnalyzeMatrix(matrix);

        // Assert
        viewModel.MemorySavingsText.ShouldContain("x savings");
        viewModel.MemoryUsageText.ShouldNotBe("-");
    }

    [Fact]
    public void ViewModel_ClearAnalysis_ResetsState()
    {
        // Arrange
        var pins = CreatePinList(10);
        var matrix = new SMatrix(pins, new List<(Guid, double)>());
        matrix.SetValues(new Dictionary<(Guid, Guid), System.Numerics.Complex>
        {
            [(pins[0], pins[1])] = new System.Numerics.Complex(1, 0)
        });

        var viewModel = new SMatrixPerformanceViewModel();
        viewModel.AnalyzeMatrix(matrix);
        viewModel.HasAnalysis.ShouldBeTrue();

        // Act
        viewModel.ClearAnalysisCommand.Execute(null);

        // Assert
        viewModel.HasAnalysis.ShouldBeFalse();
        viewModel.MatrixSizeText.ShouldBe("-");
        viewModel.StatusText.ShouldBe("Analysis cleared");
    }

    [Fact]
    public void ViewModel_UpdatesWhenMatrixChanges()
    {
        // Arrange
        var pins = CreatePinList(10);
        var matrix1 = new SMatrix(pins, new List<(Guid, double)>());
        matrix1.SetValues(new Dictionary<(Guid, Guid), System.Numerics.Complex>
        {
            [(pins[0], pins[1])] = new System.Numerics.Complex(1, 0),
            [(pins[1], pins[2])] = new System.Numerics.Complex(1, 0)
        });

        var viewModel = new SMatrixPerformanceViewModel();
        viewModel.AnalyzeMatrix(matrix1);
        viewModel.NonZeroElementsText.ShouldContain("2");

        // Act - Analyze different matrix
        var matrix2 = new SMatrix(pins, new List<(Guid, double)>());
        matrix2.SetValues(new Dictionary<(Guid, Guid), System.Numerics.Complex>
        {
            [(pins[0], pins[1])] = new System.Numerics.Complex(1, 0),
            [(pins[1], pins[2])] = new System.Numerics.Complex(1, 0),
            [(pins[2], pins[3])] = new System.Numerics.Complex(1, 0),
            [(pins[3], pins[4])] = new System.Numerics.Complex(1, 0),
            [(pins[4], pins[5])] = new System.Numerics.Complex(1, 0)
        });
        viewModel.AnalyzeMatrix(matrix2);

        // Assert - Should reflect new matrix
        viewModel.NonZeroElementsText.ShouldContain("5");
    }

    [Fact]
    public void ViewModel_FormatsNumbersWithThousandsSeparators()
    {
        // Arrange
        var pins = CreatePinList(100); // 10,000 elements
        var matrix = new SMatrix(pins, new List<(Guid, double)>());

        var transfers = new Dictionary<(Guid, Guid), System.Numerics.Complex>();
        for (int i = 0; i < 100; i++)
        {
            transfers[(pins[i], pins[(i + 1) % 100])] = new System.Numerics.Complex(0.5, 0);
        }
        matrix.SetValues(transfers);

        var viewModel = new SMatrixPerformanceViewModel();

        // Act
        viewModel.AnalyzeMatrix(matrix);

        // Assert - Should have thousand separators (locale-specific: "," or ".")
        viewModel.TotalElementsText.ShouldContain("10");
        (viewModel.TotalElementsText.Contains("10,000") || viewModel.TotalElementsText.Contains("10.000")).ShouldBeTrue();
    }

    [Fact]
    public void EndToEnd_SparsityImprovement_IsVisible()
    {
        // Arrange - Simulate a typical photonic circuit with 30 components, 4 pins each
        var pins = CreatePinList(120); // 120 pins total
        var matrix = new SMatrix(pins, new List<(Guid, double)>());

        // Typical connectivity: each component connects to ~2 neighbors
        // 30 components × 4 pins × 2 connections = ~240 non-zero entries out of 14,400
        var transfers = new Dictionary<(Guid, Guid), System.Numerics.Complex>();
        for (int i = 0; i < 240; i++)
        {
            var from = pins[i % 120];
            var to = pins[(i + 1) % 120];
            transfers[(from, to)] = new System.Numerics.Complex(0.8, 0.1);
        }
        matrix.SetValues(transfers);

        var analyzer = new SMatrixStatisticsAnalyzer();
        var viewModel = new SMatrixPerformanceViewModel();

        // Act
        var stats = analyzer.AnalyzeMatrix(matrix);
        var savings = analyzer.CalculateMemorySavings(stats);
        viewModel.AnalyzeMatrix(matrix);

        // Assert - Sparse should show significant improvement
        stats.SparsityPercentage.ShouldBeGreaterThan(95.0); // >95% sparse
        savings.ShouldBeGreaterThan(10.0); // >10x memory savings
        viewModel.StorageTypeText.ShouldContain("Sparse");
        viewModel.MemorySavingsText.ShouldContain("x savings");
    }

    /// <summary>
    /// Helper to create a list of random pin GUIDs.
    /// </summary>
    private List<Guid> CreatePinList(int count)
    {
        var pins = new List<Guid>();
        for (int i = 0; i < count; i++)
        {
            pins.Add(Guid.NewGuid());
        }
        return pins;
    }
}
