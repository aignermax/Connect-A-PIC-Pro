using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.Process;

public class ProcessFingerprintFactoryTests
{
    [Fact]
    public void From_PdkWithProcess_ExtractsMaterialsThicknessAndWavelength()
    {
        var draft = new PdkDraft
        {
            Name = "Demo", DefaultWavelengthNm = 1550,
            Process = new ProcessDefinition
            {
                Name = "SOI 220", CoreThicknessNm = 220,
                Materials =
                {
                    new ProcessMaterial { Name = "Si",   Role = "core" },
                    new ProcessMaterial { Name = "SiO2", Role = "cladding" },
                },
            },
        };

        var fp = ProcessFingerprintFactory.From(draft);

        fp.CoreMaterial.ShouldBe("Si");
        fp.Cladding.ShouldBe("SiO2");
        fp.CoreThicknessNm.ShouldBe(220);
        fp.DesignWavelengthNm.ShouldBe(1550);
        fp.ProcessName.ShouldBe("SOI 220");
        fp.IsSpecified.ShouldBeTrue();
    }

    [Fact]
    public void From_PdkWithoutProcess_IsUnspecified()
    {
        var fp = ProcessFingerprintFactory.From(new PdkDraft { Name = "Legacy", DefaultWavelengthNm = 1550 });
        fp.IsSpecified.ShouldBeFalse();
        fp.DesignWavelengthNm.ShouldBe(1550);
    }
}
