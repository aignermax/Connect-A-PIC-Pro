using CAP_Core.Analysis.MonteCarloAnalysis.FabricationVariance;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.MonteCarloAnalysis.FabricationVariance
{
    public class ComponentVarianceClassifierTests
    {
        [Theory]
        [InlineData("placeCell_MMI2x2", ComponentVarianceKind.Mmi)]
        [InlineData("bend_euler", ComponentVarianceKind.Bend)]
        [InlineData("placeCell_Curve90", ComponentVarianceKind.Bend)]
        [InlineData("directional_coupler", ComponentVarianceKind.Coupler)]
        [InlineData("y_splitter", ComponentVarianceKind.Coupler)]
        [InlineData("placeCell_StraightWG", ComponentVarianceKind.Straight)]
        [InlineData("crossing_te", ComponentVarianceKind.Generic)]
        public void Classify_MapsNazcaNameToKind(string nazcaName, ComponentVarianceKind expected)
        {
            var component = TestComponentFactory.CreateStraightWaveGuide();
            component.NazcaFunctionName = nazcaName;
            component.Name = "comp1";

            ComponentVarianceClassifier.Classify(component).ShouldBe(expected);
        }

        [Fact]
        public void Classify_MmiWins_WhenSeveralKeywordsMatch()
        {
            var component = TestComponentFactory.CreateStraightWaveGuide();
            component.NazcaFunctionName = "mmi_splitter_straight";
            component.Name = "comp1";

            ComponentVarianceClassifier.Classify(component)
                .ShouldBe(ComponentVarianceKind.Mmi);
        }
    }
}
