using System.Text.RegularExpressions;
using CAP.Avalonia.Services.Localization;
using Shouldly;
using Xunit;

namespace UnitTests.Services.Localization;

/// <summary>
/// Placeholder-parity guard for the fan-out level report strings (issue #1011):
/// the completeness tests pin the key set per language, but a translator can still
/// drop a <c>{1}</c> inside a value — silently losing the per-branch power from the
/// panel — or add one, which throws <see cref="FormatException"/> at runtime. Every
/// shipped language must carry exactly English's placeholder indices, and every
/// string must format cleanly with the values the ViewModel passes.
/// </summary>
public partial class FanOutSplitLineLocalizationTests
{
    private const string SplitLineKey = "LogicPanel.FanOutWarning.SplitLine";
    private const string StillOneKey = "LogicPanel.FanOutWarning.BranchStillOne";
    private const string WouldFailKey = "LogicPanel.FanOutWarning.BranchWouldFail";

    [GeneratedRegex(@"\{(\d+)(:[^}]*)?\}")]
    private static partial Regex PlaceholderRegex();

    [Theory]
    [InlineData(SplitLineKey)]
    [InlineData(StillOneKey)]
    [InlineData(WouldFailKey)]
    public void EveryLanguage_CarriesExactlyEnglishsPlaceholderIndices(string key)
    {
        var english = LocalizationResourceLoader.Load(SupportedLanguage.English.Code);
        var expected = PlaceholderIndices(english[key]);

        foreach (var language in SupportedLanguage.All)
        {
            var table = LocalizationResourceLoader.Load(language.Code);
            PlaceholderIndices(table[key]).ShouldBe(expected,
                $"{language.Code}:{key} must keep placeholders {string.Join(", ", expected)} — " +
                "a dropped index silently loses a number from the panel, an added one throws at runtime");
        }
    }

    [Fact]
    public void EveryLanguage_FormatsCleanly_WithTheValuesTheViewModelPasses()
    {
        // Mirrors LogicFanOutWarningViewModel: the split line gets (load count,
        // driver power, per-branch power, split loss in dB); the branch lines get
        // (load name, threshold).
        var argsPerKey = new Dictionary<string, object[]>
        {
            [SplitLineKey] = new object[] { 4, 1.0, 0.25, 6.0206 },
            [StillOneKey] = new object[] { "NAND1.A", 0.125 },
            [WouldFailKey] = new object[] { "NAND1.A", 0.125 },
        };

        foreach (var language in SupportedLanguage.All)
        {
            var table = LocalizationResourceLoader.Load(language.Code);
            foreach (var (key, args) in argsPerKey)
            {
                var formatted = string.Format(table[key], args);
                formatted.ShouldNotBeNullOrWhiteSpace();
                formatted.Contains('{').ShouldBeFalse($"{language.Code}:{key} left an unformatted placeholder");
            }
        }
    }

    /// <summary>The sorted placeholder indices (<c>{0}</c>, <c>{2:0.###}</c>) of a format string.</summary>
    private static int[] PlaceholderIndices(string format) =>
        PlaceholderRegex().Matches(format)
            .Select(match => int.Parse(match.Groups[1].Value))
            .OrderBy(index => index)
            .ToArray();
}
