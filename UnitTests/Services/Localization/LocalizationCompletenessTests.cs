using System.Text.RegularExpressions;
using CAP.Avalonia.Services.Localization;
using Shouldly;
using Xunit;

namespace UnitTests.Services.Localization;

/// <summary>
/// Guards translation completeness: every shipped language covers exactly the English
/// key set (English is the source of truth), and every <c>{loc:Localize Key}</c> used
/// in AXAML exists in English — so raw keys can never leak into the UI.
/// </summary>
public partial class LocalizationCompletenessTests
{
    [GeneratedRegex(@"\{loc:Localize\s+([A-Za-z0-9_.]+)\}")]
    private static partial Regex LocalizeUsageRegex();

    [Fact]
    public void EnglishTable_LoadsAndIsNonEmpty()
    {
        var en = LocalizationResourceLoader.Load(SupportedLanguage.English.Code);

        en.Count.ShouldBeGreaterThan(50);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("zh-Hans")]
    [InlineData("es")]
    public void EveryLanguage_CoversExactlyTheEnglishKeySet(string code)
    {
        var en = LocalizationResourceLoader.Load(SupportedLanguage.English.Code);
        var table = LocalizationResourceLoader.Load(code);

        var missing = en.Keys.Except(table.Keys).ToList();
        var extra = table.Keys.Except(en.Keys).ToList();

        missing.ShouldBeEmpty($"keys missing in {code}: {string.Join(", ", missing)}");
        extra.ShouldBeEmpty($"orphan keys in {code}: {string.Join(", ", extra)}");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("zh-Hans")]
    [InlineData("es")]
    public void EveryLanguage_HasNoEmptyValues(string code)
    {
        var table = LocalizationResourceLoader.Load(code);

        var empty = table.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();

        empty.ShouldBeEmpty($"empty values in {code}: {string.Join(", ", empty)}");
    }

    [Fact]
    public void EveryLocalizeKeyUsedInAxaml_ExistsInEnglish()
    {
        var en = LocalizationResourceLoader.Load(SupportedLanguage.English.Code);
        var viewsDirectory = FindAvaloniaProjectDirectory();

        var unknownKeys = Directory
            .EnumerateFiles(viewsDirectory, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(file => LocalizeUsageRegex().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value))
            .Distinct()
            .Where(key => !en.ContainsKey(key))
            .ToList();

        unknownKeys.ShouldBeEmpty($"AXAML uses keys missing in strings-en.json: {string.Join(", ", unknownKeys)}");
    }

    /// <summary>Walks up from the test binary to the repo checkout containing CAP.Avalonia.</summary>
    private static string FindAvaloniaProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "CAP.Avalonia");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("CAP.Avalonia project directory not found above test binaries.");
    }
}
