using CAP.Avalonia.Services.Localization;
using Shouldly;
using Xunit;

namespace UnitTests.Services.Localization;

/// <summary>
/// Pins the bus-view help strings (issue #1068): the Logic panel help flyout states in
/// every shipped locale that index 0 is the least-significant bit of a bus. The generic
/// key-set parity tests in <see cref="LocalizationCompletenessTests"/> cover presence;
/// these tests pin the education sentence itself.
/// </summary>
public class LogicPanelBusHelpLocalizationTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("ja")]
    [InlineData("zh-Hans")]
    public void BusHelp_EveryLocale_ShipsTitleAndBody(string code)
    {
        var table = LocalizationResourceLoader.Load(code);

        table["LogicPanelHelp.BusTitle"].ShouldNotBeNullOrWhiteSpace();
        table["LogicPanelHelp.BusBody"].ShouldNotBeNullOrWhiteSpace();
        table["LogicPanelHelp.BusBody"].ShouldContain("A = 3 (0011)", Case.Sensitive,
            "every locale keeps the worked example the panel shows");
    }

    [Fact]
    public void BusHelp_English_StatesIndexZeroIsLeastSignificantBit()
    {
        var en = LocalizationResourceLoader.Load(SupportedLanguage.English.Code);

        en["LogicPanelHelp.BusBody"].ShouldContain("least-significant bit");
        en["LogicPanelHelp.BusBody"].ShouldContain("Index 0");
    }
}
