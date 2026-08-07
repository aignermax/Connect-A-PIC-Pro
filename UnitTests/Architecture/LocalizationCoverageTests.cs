using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace UnitTests.Architecture;

/// <summary>
/// Guards that new UI keeps its text localizable: every user-visible AXAML attribute must bind
/// (<c>{loc:Localize …}</c> / <c>{Binding …}</c> / <c>{DynamicResource …}</c>) rather than carry a
/// hardcoded literal. A forgotten <c>{loc:Localize}</c> on a new feature fails this test with the
/// exact file:line — the string tables alone (LocalizationCompletenessTests) can't catch a string
/// that was never routed through them. Only covers AXAML: code-drawn (canvas HUD) and ViewModel
/// status strings live outside AXAML and are a review-time concern.
/// </summary>
public class LocalizationCoverageTests
{
    /// <summary>Text-bearing attributes whose literal values would show up untranslated.</summary>
    private static readonly Regex TextAttribute = new(
        "(?<![A-Za-z])(?:Text|Content|Header|Watermark|ToolTip\\.Tip|Title)=\"([^\"]*)\"",
        RegexOptions.Compiled);

    /// <summary>Needs at least one 2+ letter run to be a real word (skips emoji, symbols, numbers, µm).</summary>
    private static readonly Regex HasWord = new("[A-Za-z]{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Literals that intentionally stay un-localized: units/symbols, tech tokens and proper nouns
    /// (product/library names), and shortcut markers. Curated on purpose — adding an entry is a
    /// deliberate, review-visible act; prefer <c>{loc:Localize}</c> unless the string is genuinely
    /// language-neutral.
    /// </summary>
    private static readonly HashSet<string> AllowedLiterals = new(StringComparer.Ordinal)
    {
        "nm", "CW", "Python", "Nazca", "gdsfactory", "SiEPIC", "PDK", "GDS", "FDTD",
        "Python:", "Nazca:", "Playground",
        // Proper noun, technical notation, symbol+unit labels, and a key-format placeholder —
        // language-neutral, deliberately not translated.
        "Lunima", "S-matrix", "n_eff:", "n_eff", "MFD:", "λ (nm):", "sk-ant-...",
        // Physical unit for relative intensity noise — language-neutral like "nm".
        "dB/Hz",
    };

    /// <summary>Read-only code snippets shown as examples are source, not UI copy — never localized.</summary>
    private static bool LooksLikeCode(string v) =>
        v.Contains("&#10;") || v.Contains("import ") || v.Contains("gf.") || v.Contains("nd.")
        || v.Contains("()") || v.Contains("component =");

    [Fact]
    public void EveryUserVisibleAxamlString_IsLocalizedOrBound()
    {
        var repoRoot = FindRepoRoot();
        var viewsRoot = Path.Combine(repoRoot, "CAP.Avalonia");
        var violations = new List<string>();

        foreach (var file in Directory.GetFiles(viewsRoot, "*.axaml", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file)) continue;
            var text = File.ReadAllText(file);
            foreach (Match m in TextAttribute.Matches(text))
            {
                var value = m.Groups[1].Value.Trim();
                if (value.Length == 0) continue;
                if (value.StartsWith("{")) continue;          // binding / markup extension
                if (!HasWord.IsMatch(value)) continue;         // emoji, symbols, numbers, units
                if (LooksLikeCode(value)) continue;
                if (AllowedLiterals.Contains(value)) continue;

                var line = text.Take(m.Index).Count(c => c == '\n') + 1;
                var rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                violations.Add($"{rel}:{line}  \"{Truncate(value)}\"");
            }
        }

        violations.ShouldBeEmpty(
            "\nHardcoded UI text found — these AXAML strings are not localized:\n\n" +
            string.Join("\n", violations.Select(v => $"  ✗ {v}")) +
            "\n\nWrap each in {loc:Localize Key} and add the key to all Assets/i18n/strings-*.json files.\n" +
            "If a string is genuinely language-neutral (a unit, symbol, or proper noun), add it to\n" +
            "LocalizationCoverageTests.AllowedLiterals — but justify it in review.");
    }

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..60] + "…";

    private static bool IsBuildOutput(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}obj{sep}") || path.Contains($"{sep}bin{sep}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root.");
    }
}
