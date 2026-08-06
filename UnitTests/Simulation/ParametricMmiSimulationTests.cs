using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_Core.Components.FormulaReading;
using Shouldly;
using Xunit;

namespace UnitTests.Simulation;

/// <summary>
/// Verifies the parametric MMI and Directional Coupler from demo-pdk.json:
/// named physical parameters (insertion loss, splitting ratio, coupling ratio)
/// drive the S-matrix, defaults are applied on placement, and instances are
/// independent of each other.
/// </summary>
public class ParametricMmiSimulationTests
{
    private const double Tolerance = 1e-9;

    private static ComponentTemplate LoadTemplate(string name) =>
        TestPdkLoader.LoadFromPdk("demo-pdk.json").First(t => t.Name == name);

    /// <summary>Resolves the connection function for a named pin pair.</summary>
    private static ConnectionFunction GetConnection(Component component, string fromPin, string toPin)
    {
        var pins = component.GetAllPins();
        var from = pins.First(p => p.Name == fromPin);
        var to = pins.First(p => p.Name == toPin);
        return component.WaveLengthToSMatrixMap[1550]
            .NonLinearConnections[(from.IDInFlow, to.IDOutFlow)];
    }

    [Fact]
    public void DemoPdk_Mmi_IsParametric_WithLabeledUnitParameters()
    {
        var template = LoadTemplate("1x2 MMI Splitter");

        template.CreateSMatrixWithSliders.ShouldNotBeNull();
        template.SliderDefinitions.Count.ShouldBe(2);
        template.ParameterDefinitions.Count.ShouldBe(2);

        var loss = template.ParameterDefinitions.First(p => p.Name == "insertion_loss");
        loss.Label.ShouldBe("Insertion Loss");
        loss.Unit.ShouldBe("dB");
        loss.SliderNumber.ShouldBe(0);

        var ratio = template.ParameterDefinitions.First(p => p.Name == "splitting_ratio");
        ratio.Label.ShouldBe("Splitting Ratio (out1)");
        ratio.Unit.ShouldBe("%");
        ratio.SliderNumber.ShouldBe(1);
    }

    [Fact]
    public void FreshMmi_SlidersStartAtParameterDefaults()
    {
        var component = ComponentTemplates.CreateFromTemplate(LoadTemplate("1x2 MMI Splitter"), 0, 0);

        component.GetSlider(0)!.Value.ShouldBe(0.3, Tolerance,
            "insertion_loss slider must start at its documented default");
        component.GetSlider(1)!.Value.ShouldBe(50, Tolerance,
            "splitting_ratio slider must start at its documented default");
        component.ParameterDefinitions.Count.ShouldBe(2,
            "parameter metadata must be copied onto the placed instance");
    }

    [Theory]
    [InlineData(0.0, 50.0, 0.5)]   // lossless, 50/50 → half the power on out1
    [InlineData(3.0, 50.0, 0.2506)] // 3 dB loss halves total power again
    [InlineData(0.0, 80.0, 0.8)]   // 80 % of the power routed to out1
    public void Mmi_Out1Power_FollowsInsertionLossAndSplittingRatio(
        double insertionLossDb, double splittingRatioPercent, double expectedPower)
    {
        var component = ComponentTemplates.CreateFromTemplate(LoadTemplate("1x2 MMI Splitter"), 0, 0);
        var connFn = GetConnection(component, "in", "out1");

        var value = connFn.CalcConnectionWeightAsync(
            new List<object> { insertionLossDb, splittingRatioPercent });

        double power = value.Magnitude * value.Magnitude;
        power.ShouldBe(expectedPower, 1e-3,
            $"out1 power at IL={insertionLossDb} dB, SR={splittingRatioPercent} %");
    }

    [Fact]
    public void Mmi_OutputPowers_SumToInsertionLossBudget()
    {
        var component = ComponentTemplates.CreateFromTemplate(LoadTemplate("1x2 MMI Splitter"), 0, 0);
        var out1 = GetConnection(component, "in", "out1");
        var out2 = GetConnection(component, "in", "out2");

        const double lossDb = 1.0;
        const double ratio = 65.0;
        var parameters = new List<object> { lossDb, ratio };

        var p1 = Math.Pow(out1.CalcConnectionWeightAsync(parameters).Magnitude, 2);
        var p2 = Math.Pow(out2.CalcConnectionWeightAsync(parameters).Magnitude, 2);

        (p1 + p2).ShouldBe(Math.Pow(10, -lossDb / 10), 1e-9,
            "total output power must equal the insertion-loss budget");
        (p1 / (p1 + p2)).ShouldBe(ratio / 100, 1e-9,
            "power split must follow the splitting ratio");
    }

    [Fact]
    public void Mmi_TwoInstances_HaveIndependentParameterState()
    {
        var template = LoadTemplate("1x2 MMI Splitter");
        var a = ComponentTemplates.CreateFromTemplate(template, 0, 0);
        var b = (Component)a.Clone();

        var aFn = GetConnection(a, "in", "out1");
        var bFn = GetConnection(b, "in", "out1");

        var aValue = aFn.CalcConnectionWeightAsync(new List<object> { 0.0, 100.0 });
        var bValue = bFn.CalcConnectionWeightAsync(new List<object> { 0.0, 0.0 });

        aValue.Magnitude.ShouldBe(1.0, Tolerance, "instance A: all power to out1");
        bValue.Magnitude.ShouldBe(0.0, Tolerance, "instance B: no power to out1");
    }

    [Fact]
    public void DirectionalCoupler_CouplingRatio_SetsCrossAndBarTransmission()
    {
        var component = ComponentTemplates.CreateFromTemplate(LoadTemplate("Directional Coupler"), 0, 0);
        var bar = GetConnection(component, "in1", "out1");
        var cross = GetConnection(component, "in1", "out2");

        // Full coupling: everything crosses with the physical 90° phase shift.
        var barValue = bar.CalcConnectionWeightAsync(new List<object> { 100.0 });
        var crossValue = cross.CalcConnectionWeightAsync(new List<object> { 100.0 });

        barValue.Magnitude.ShouldBe(0.0, Tolerance);
        crossValue.Magnitude.ShouldBe(1.0, Tolerance);
        crossValue.Imaginary.ShouldBe(1.0, Tolerance, "cross path carries a 90° phase");

        // 50/50: both paths at 1/sqrt(2).
        var barAt50 = bar.CalcConnectionWeightAsync(new List<object> { 50.0 });
        barAt50.Magnitude.ShouldBe(Math.Sqrt(0.5), Tolerance);
    }

    [Fact]
    public void DirectionalCoupler_FreshInstance_StartsAtDefaultCoupling()
    {
        var component = ComponentTemplates.CreateFromTemplate(LoadTemplate("Directional Coupler"), 0, 0);

        component.GetSlider(0)!.Value.ShouldBe(50, Tolerance);
        component.ParameterDefinitions.Count.ShouldBe(1);
        component.ParameterDefinitions[0].Unit.ShouldBe("%");
    }

    [Fact]
    public void ClonedMmi_KeepsParameterDefinitions()
    {
        var original = ComponentTemplates.CreateFromTemplate(LoadTemplate("1x2 MMI Splitter"), 0, 0);
        var clone = (Component)original.Clone();

        clone.ParameterDefinitions.Count.ShouldBe(2,
            "copy/paste and grouping flows rely on cloned parameter metadata");
        clone.GetAllSliders().Count.ShouldBe(2);
    }
}
