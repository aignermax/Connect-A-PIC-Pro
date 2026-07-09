using CAP_Core.LightCalculation.TimeDomainSimulation.CompactModels;
using CAP_Core.LightCalculation.TimeDomainSimulation.CompactModels.Models;
using Shouldly;
using System.Numerics;
using Xunit;

namespace UnitTests.LightCalculation.TimeDomainSimulation.CompactModels;

public class CompactModelRegistryTests
{
    [Theory]
    [InlineData(LaserDiodeRateEquationModel.ModelName, typeof(LaserDiodeRateEquationModel))]
    [InlineData(PhotodiodeRcModel.ModelName, typeof(PhotodiodeRcModel))]
    [InlineData(ElectroOpticPhaseModulatorModel.ModelName, typeof(ElectroOpticPhaseModulatorModel))]
    public void Create_RegisteredName_ReturnsCorrectModelType(string name, Type expectedType)
    {
        var model = CompactModelRegistry.Create(name);

        model.ShouldBeOfType(expectedType);
        model.Name.ShouldBe(name);
        CompactModelRegistry.IsRegistered(name).ShouldBeTrue();
    }

    [Fact]
    public void Create_UnknownName_ThrowsWithClearMessage()
    {
        var ex = Should.Throw<InvalidOperationException>(
            () => CompactModelRegistry.Create("Unknown"));

        ex.Message.ShouldContain("Unknown compact model 'Unknown'");
        ex.Message.ShouldContain(PhotodiodeRcModel.ModelName);
    }

    [Fact]
    public void IsRegistered_UnknownName_ReturnsFalse()
    {
        CompactModelRegistry.IsRegistered("NoSuchModel").ShouldBeFalse();
    }

    [Fact]
    public void Create_WithParameters_PassesThemToModel()
    {
        const double customVPi = 7.5;
        var model = CompactModelRegistry.Create(
            ElectroOpticPhaseModulatorModel.ModelName,
            new Dictionary<string, double>
            {
                [ElectroOpticPhaseModulatorModel.VPiKey] = customVPi,
            });

        ((ElectroOpticPhaseModulatorModel)model).VPiVolts.ShouldBe(customVPi);
    }

    [Fact]
    public void ActiveComponentStepper_WrongTraceLength_Throws()
    {
        var model = CompactModelRegistry.Create(PhotodiodeRcModel.ModelName);

        Should.Throw<ArgumentException>(() => ActiveComponentStepper.StepOverTrace(
            model, 1e-12, sampleCount: 10, incidentField: new Complex[5]));
        Should.Throw<ArgumentException>(() => ActiveComponentStepper.StepOverTrace(
            model, 1e-12, sampleCount: 10, electricalInput: new double[3]));
    }
}
