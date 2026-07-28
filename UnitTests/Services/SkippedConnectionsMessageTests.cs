using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using Shouldly;
using Xunit;

namespace UnitTests.Services;

/// <summary>
/// <see cref="SkippedConnectionsMessage"/> formats the shared "N connections skipped" export
/// warning from a plain description list — pure formatting, no canvas rescanning (that
/// responsibility belongs solely to the exporters' <c>skippedConnections</c> out-parameter).
/// </summary>
public class SkippedConnectionsMessageTests
{
    public SkippedConnectionsMessageTests()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
    }

    [Fact]
    public void Build_NoSkippedConnections_ReturnsNull()
    {
        SkippedConnectionsMessage.Build(new List<string>()).ShouldBeNull();
    }

    [Fact]
    public void Build_FewSkippedConnections_NamesAllOfThem()
    {
        var message = SkippedConnectionsMessage.Build(new List<string> { "A.p0 → B.p0", "C.p0 → D.p0" });

        message.ShouldNotBeNull();
        message.ShouldContain("2 connection(s)");
        message.ShouldContain("A.p0 → B.p0");
        message.ShouldContain("C.p0 → D.p0");
        message.ShouldNotContain("more");
    }

    [Fact]
    public void Build_ManySkippedConnections_CapsAtFiveNamedPlusAndMoreSuffix()
    {
        var names = Enumerable.Range(1, 8).Select(i => $"Comp{i}.p0 → Comp{i + 1}.p0").ToList();

        var message = SkippedConnectionsMessage.Build(names);

        message.ShouldNotBeNull();
        message.ShouldContain("8 connection(s)");
        for (int i = 0; i < 5; i++)
            message.ShouldContain(names[i]);
        for (int i = 5; i < 8; i++)
            message.ShouldNotContain(names[i]);
        message.ShouldContain("and 3 more");
    }
}
