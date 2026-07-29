using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using Shouldly;
using Xunit;

namespace UnitTests.Services;

/// <summary>
/// <see cref="ExportWarningMessages"/> formats the export flow's post-write warnings from
/// plain description lists — pure formatting, no canvas rescanning (that responsibility
/// belongs solely to the exporters' <c>skippedConnections</c>/<c>unresolvedCrossings</c>
/// out-parameters).
/// </summary>
public class ExportWarningMessagesTests
{
    public ExportWarningMessagesTests()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
    }

    [Fact]
    public void BuildSkipped_NoSkippedConnections_ReturnsNull()
    {
        ExportWarningMessages.BuildSkipped(new List<string>()).ShouldBeNull();
    }

    [Fact]
    public void BuildSkipped_FewSkippedConnections_NamesAllOfThem()
    {
        var message = ExportWarningMessages.BuildSkipped(new List<string> { "A.p0 → B.p0", "C.p0 → D.p0" });

        message.ShouldNotBeNull();
        message.ShouldContain("2 connection(s)");
        message.ShouldContain("A.p0 → B.p0");
        message.ShouldContain("C.p0 → D.p0");
        message.ShouldNotContain("more");
    }

    [Fact]
    public void BuildSkipped_ManySkippedConnections_CapsAtFiveNamedPlusAndMoreSuffix()
    {
        var names = Enumerable.Range(1, 8).Select(i => $"Comp{i}.p0 → Comp{i + 1}.p0").ToList();

        var message = ExportWarningMessages.BuildSkipped(names);

        message.ShouldNotBeNull();
        message.ShouldContain("8 connection(s)");
        for (int i = 0; i < 5; i++)
            message.ShouldContain(names[i]);
        for (int i = 5; i < 8; i++)
            message.ShouldNotContain(names[i]);
        message.ShouldContain("and 3 more");
    }

    [Fact]
    public void BuildUnresolvedCrossings_NoCandidates_ReturnsNull()
    {
        ExportWarningMessages.BuildUnresolvedCrossings(new List<string>()).ShouldBeNull();
    }

    [Fact]
    public void BuildUnresolvedCrossings_UsesItsOwnLocalizationKey_DistinctFromSkipped()
    {
        var skipped = ExportWarningMessages.BuildSkipped(new List<string> { "A.p0 → B.p0" });
        var unresolved = ExportWarningMessages.BuildUnresolvedCrossings(new List<string> { "A.p0 → B.p0" });

        skipped.ShouldNotBeNull();
        unresolved.ShouldNotBeNull();
        skipped.ShouldNotBe(unresolved);
        unresolved.ShouldContain("1 connection(s)");
        unresolved.ShouldContain("A.p0 → B.p0");
    }
}
