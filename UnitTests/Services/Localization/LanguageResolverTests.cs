using System.Globalization;
using CAP.Avalonia.Services.Localization;
using Shouldly;
using Xunit;

namespace UnitTests.Services.Localization;

/// <summary>
/// Tests OS-culture → shipped-language mapping, including regional variants
/// (de-AT → de, zh-CN → zh-Hans, es-MX → es) and the English fallback.
/// </summary>
public class LanguageResolverTests
{
    [Theory]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("de", "de")]
    [InlineData("de-DE", "de")]
    [InlineData("de-AT", "de")]
    [InlineData("de-CH", "de")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("zh-Hant", "zh-Hans")]
    [InlineData("es", "es")]
    [InlineData("es-ES", "es")]
    [InlineData("es-MX", "es")]
    [InlineData("es-AR", "es")]
    public void Resolve_MapsRegionalVariantsToShippedLanguage(string cultureName, string expected)
    {
        var result = LanguageResolver.Resolve(new CultureInfo(cultureName));

        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("ja-JP")]
    [InlineData("pt-BR")]
    [InlineData("ru-RU")]
    public void Resolve_UnsupportedCulture_FallsBackToEnglish(string cultureName)
    {
        var result = LanguageResolver.Resolve(new CultureInfo(cultureName));

        result.ShouldBe(SupportedLanguage.English.Code);
    }

    [Fact]
    public void Resolve_InvariantCulture_FallsBackToEnglish()
    {
        var result = LanguageResolver.Resolve(CultureInfo.InvariantCulture);

        result.ShouldBe(SupportedLanguage.English.Code);
    }
}
