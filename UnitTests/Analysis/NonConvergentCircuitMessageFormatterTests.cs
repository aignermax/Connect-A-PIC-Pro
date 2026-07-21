using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Analysis;
using CAP_Core.LightCalculation;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis;

/// <summary>
/// Field round 4, final review batch, findings [5] and [9]: numbers embedded in the
/// localized physics-abort messages must follow the culture of the ACTIVE UI language
/// (a German sentence shows "2,1 %", not the invariant "2.1"), and a resonant loop
/// whose components could not be named still gets a fully localized message instead of
/// raw English inside the localized "Failed: {0}" wrapper.
/// </summary>
[Collection("LocalizationSingleton")]
public class NonConvergentCircuitMessageFormatterTests : IDisposable
{
    public NonConvergentCircuitMessageFormatterTests()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
    }

    public void Dispose()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
    }

    [Fact]
    public void Format_NonPassiveComponent_German_UsesGermanDecimalSeparator()
    {
        LocalizationService.Instance.SetLanguage("de");
        var ex = new NonConvergentCircuitException(
            "english core message",
            NonConvergentCircuitKind.NonPassiveComponent,
            componentName: "Bad_DC",
            wavelengthNm: 1550,
            excessPercent: 2.1);

        var message = NonConvergentCircuitMessageFormatter.Format(ex);

        message.ShouldContain("Bad_DC");
        message.ShouldContain("2,1", customMessage: "German text must use the German decimal comma");
        message.ShouldNotContain("2.1");
    }

    [Fact]
    public void Format_EnergyFabricated_Spanish_UsesSpanishDecimalSeparator()
    {
        LocalizationService.Instance.SetLanguage("es");
        var ex = new NonConvergentCircuitException(
            "english core message",
            NonConvergentCircuitKind.EnergyFabricated,
            wavelengthNm: 1310,
            excessPercent: 6.0);

        var message = NonConvergentCircuitMessageFormatter.Format(ex);

        message.ShouldContain("6,0");
        message.ShouldContain("1310");
    }

    [Fact]
    public void Format_ResonantLoopWithoutNames_IsStillFullyLocalized()
    {
        LocalizationService.Instance.SetLanguage("de");
        var ex = new NonConvergentCircuitException(
            "This circuit has no steady state — a lossless feedback loop sits exactly on resonance.",
            NonConvergentCircuitKind.ResonantLoop,
            loopComponentNames: Array.Empty<string>(),
            wavelengthNm: 1550);

        var message = NonConvergentCircuitMessageFormatter.Format(ex);

        message.ShouldContain("1550");
        message.ShouldContain("Rückkopplungsschleife");
        message.ShouldNotContain("no steady state",
            customMessage: "the unnamed-loop case must not fall back to the English core message");
    }

    [Fact]
    public void Format_ResonantLoopWithNames_KeepsNamingTheLoop()
    {
        var ex = new NonConvergentCircuitException(
            "core",
            NonConvergentCircuitKind.ResonantLoop,
            loopComponentNames: new[] { "Coupler_1", "Coupler_2" },
            wavelengthNm: 1550);

        var message = NonConvergentCircuitMessageFormatter.Format(ex);

        message.ShouldContain("Coupler_1");
        message.ShouldContain("Coupler_2");
        message.ShouldContain("1550");
    }

    [Fact]
    public void Format_ConnectionGain_HasALocalizedMessageNamingBothEnds()
    {
        var ex = new NonConvergentCircuitException(
            "core",
            NonConvergentCircuitKind.ConnectionGain,
            componentName: "'Splitter_A' → 'Coupler_B'",
            wavelengthNm: 1550,
            excessPercent: 2.0);

        var message = NonConvergentCircuitMessageFormatter.Format(ex);

        message.ShouldContain("Splitter_A");
        message.ShouldContain("Coupler_B");
        message.ShouldContain("1550");
        message.ShouldNotContain("Failed", customMessage: "must not use the generic fallback wrapper");
    }

    [Fact]
    public void Format_MissingStructuredFields_FallsBackToTheWrappedCoreMessage()
    {
        var ex = new NonConvergentCircuitException("core message only");

        var message = NonConvergentCircuitMessageFormatter.Format(ex);

        message.ShouldContain("core message only");
    }
}
