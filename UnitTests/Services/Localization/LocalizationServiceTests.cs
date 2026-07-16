using System.Globalization;
using CAP.Avalonia.Services.Localization;
using Shouldly;
using Xunit;

namespace UnitTests.Services.Localization;

/// <summary>
/// Tests the runtime translation behavior: per-key English fallback, key-as-last-resort,
/// "system" resolution, unknown-code safety and live-switch change notifications.
/// </summary>
public class LocalizationServiceTests
{
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Tables = new()
    {
        ["en"] = new Dictionary<string, string> { ["Hello"] = "Hello", ["OnlyEnglish"] = "English only" },
        ["de"] = new Dictionary<string, string> { ["Hello"] = "Hallo" },
        ["zh-Hans"] = new Dictionary<string, string> { ["Hello"] = "你好" },
        ["es"] = new Dictionary<string, string> { ["Hello"] = "Hola" },
    };

    private static LocalizationService CreateService(string systemCulture = "en-US") =>
        new(code => Tables.TryGetValue(code, out var t) ? t : new Dictionary<string, string>(),
            () => new CultureInfo(systemCulture));

    [Fact]
    public void Translate_ActiveLanguage_ReturnsTranslation()
    {
        var service = CreateService();
        service.SetLanguage("de");

        service.Translate("Hello").ShouldBe("Hallo");
    }

    [Fact]
    public void Translate_MissingInActiveLanguage_FallsBackToEnglish()
    {
        var service = CreateService();
        service.SetLanguage("de");

        service.Translate("OnlyEnglish").ShouldBe("English only");
    }

    [Fact]
    public void Translate_MissingEverywhere_ReturnsKey()
    {
        var service = CreateService();

        service.Translate("No.Such.Key").ShouldBe("No.Such.Key");
    }

    [Fact]
    public void Indexer_ReturnsSameAsTranslate()
    {
        var service = CreateService();
        service.SetLanguage("zh-Hans");

        service["Hello"].ShouldBe("你好");
    }

    [Fact]
    public void Constructor_ResolvesSystemCulture()
    {
        var service = CreateService("de-AT");

        service.ActiveLanguageCode.ShouldBe("de");
    }

    [Fact]
    public void SetLanguage_System_ResolvesOsCulture()
    {
        var service = CreateService("zh-CN");
        service.SetLanguage("es");

        service.SetLanguage(LocalizationService.SystemLanguageCode);

        service.ActiveLanguageCode.ShouldBe("zh-Hans");
    }

    [Theory]
    [InlineData("tlh")]
    [InlineData("")]
    [InlineData(null)]
    public void SetLanguage_UnknownOrEmpty_NeverThrows(string? code)
    {
        var service = CreateService();

        Should.NotThrow(() => service.SetLanguage(code));
        SupportedLanguage.IsSupportedCode(service.ActiveLanguageCode).ShouldBeTrue();
    }

    [Fact]
    public void SetLanguage_UnknownCode_FallsBackToEnglish()
    {
        var service = CreateService("de-DE");
        service.SetLanguage("tlh");

        service.ActiveLanguageCode.ShouldBe("en");
    }

    [Fact]
    public void SetLanguage_Change_RaisesIndexerNotification()
    {
        var service = CreateService();
        var raised = new List<string?>();
        service.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        service.SetLanguage("es");

        raised.ShouldContain(nameof(LocalizationService.ActiveLanguageCode));
        raised.ShouldContain("Item[]");
    }

    [Fact]
    public void SetLanguage_SameLanguage_RaisesNothing()
    {
        var service = CreateService();
        var raised = 0;
        service.PropertyChanged += (_, _) => raised++;

        service.SetLanguage("en");

        raised.ShouldBe(0);
    }
}
