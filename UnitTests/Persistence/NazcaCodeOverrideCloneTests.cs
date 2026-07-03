using CAP_DataAccess.Persistence.PIR;
using Shouldly;
using Xunit;

namespace UnitTests.Persistence;

/// <summary>Tests for <see cref="NazcaCodeOverride.Clone"/> deep-copy semantics.</summary>
public class NazcaCodeOverrideCloneTests
{
    [Fact]
    public void Clone_CopiesAllScalarFields()
    {
        var src = new NazcaCodeOverride
        {
            FunctionName = "fn",
            FunctionParameters = "p=1",
            ModuleName = "mod",
            TemplateFunctionName = "tfn",
            TemplateFunctionParameters = "tp=2",
            TemplateModuleName = "tmod",
            RawCode = "import nazca",
            Backend = OverrideBackend.GdsFactory,
            OverrideWidthMicrometers = 5.5,
            OverrideHeightMicrometers = 3.3,
            HasNoSimulationModel = true,
            OverrideBboxXMinMicrometers = -1.0,
            OverrideBboxYMaxMicrometers = 2.0,
        };

        var copy = src.Clone();

        copy.FunctionName.ShouldBe("fn");
        copy.FunctionParameters.ShouldBe("p=1");
        copy.ModuleName.ShouldBe("mod");
        copy.TemplateFunctionName.ShouldBe("tfn");
        copy.TemplateFunctionParameters.ShouldBe("tp=2");
        copy.TemplateModuleName.ShouldBe("tmod");
        copy.RawCode.ShouldBe("import nazca");
        copy.Backend.ShouldBe(OverrideBackend.GdsFactory);
        copy.OverrideWidthMicrometers.ShouldBe(5.5);
        copy.OverrideHeightMicrometers.ShouldBe(3.3);
        copy.HasNoSimulationModel.ShouldBeTrue();
        copy.OverrideBboxXMinMicrometers.ShouldBe(-1.0);
        copy.OverrideBboxYMaxMicrometers.ShouldBe(2.0);
    }

    [Fact]
    public void Clone_DeepCopiesPinLists_SoMutatingCopyDoesNotAffectOriginal()
    {
        var src = new NazcaCodeOverride
        {
            OverridePins = new() { new OverridePinData { Name = "a0", OffsetXMicrometers = 1 } },
            TemplatePins = new() { new OverridePinData { Name = "t0" } },
        };

        var copy = src.Clone();

        copy.OverridePins.ShouldNotBeSameAs(src.OverridePins);
        copy.OverridePins![0].ShouldNotBeSameAs(src.OverridePins![0]);
        copy.OverridePins[0].Name.ShouldBe("a0");
        copy.TemplatePins![0].Name.ShouldBe("t0");

        copy.OverridePins[0].Name = "changed";
        src.OverridePins[0].Name.ShouldBe("a0", "Mutating the clone's pin must not affect the source.");
    }

    [Fact]
    public void Clone_NullPinLists_StayNull()
    {
        var copy = new NazcaCodeOverride { RawCode = "x" }.Clone();

        copy.OverridePins.ShouldBeNull();
        copy.TemplatePins.ShouldBeNull();
    }
}
