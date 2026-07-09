using System.Globalization;
using CAP_Core.Analysis.EyeDiagram;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.EyeDiagram;

public class EyeDiagramBuilderTests
{
    private const double SampleRate = 1e12;
    private const int SamplesPerBit = 20;
    private const double BitPeriod = SamplesPerBit / SampleRate;

    private static double[] AlternatingNrzTrace(int bitCount, double amplitude = 1.0)
    {
        var bits = Enumerable.Range(0, bitCount).Select(i => i % 2 == 0).ToArray();
        return PrbsGenerator.ToNrzSamples(bits, SamplesPerBit, amplitude);
    }

    [Fact]
    public void Build_TotalCountsEqualNonSkippedSamples()
    {
        var trace = AlternatingNrzTrace(bitCount: 32);

        var histogram = EyeDiagramBuilder.Build(trace, SampleRate, BitPeriod, skipBits: 2);

        int total = 0;
        for (int t = 0; t < histogram.TimeBinCount; t++)
            for (int a = 0; a < histogram.AmplitudeBinCount; a++)
                total += histogram.Counts[t, a];

        total.ShouldBe(trace.Length - 2 * SamplesPerBit);
    }

    [Fact]
    public void Build_TwoLevelSignal_OccupiesOnlyExtremeAmplitudeBins()
    {
        var trace = AlternatingNrzTrace(bitCount: 32);

        var histogram = EyeDiagramBuilder.Build(trace, SampleRate, BitPeriod, amplitudeBins: 8);

        for (int t = 0; t < histogram.TimeBinCount; t++)
            for (int a = 1; a < histogram.AmplitudeBinCount - 1; a++)
                histogram.Counts[t, a].ShouldBe(0, $"unexpected count in middle bin ({t},{a})");
    }

    [Fact]
    public void Build_AmplitudeRange_MatchesTraceExtremes()
    {
        var trace = AlternatingNrzTrace(bitCount: 32, amplitude: 2.5);

        var histogram = EyeDiagramBuilder.Build(trace, SampleRate, BitPeriod);

        histogram.MinAmplitude.ShouldBe(0);
        histogram.MaxAmplitude.ShouldBe(2.5);
        histogram.BitPeriodSeconds.ShouldBe(BitPeriod);
    }

    [Fact]
    public void Build_ConstantTrace_PutsAllCountsInFirstAmplitudeBin()
    {
        var trace = Enumerable.Repeat(1.0, 200).ToArray();

        var histogram = EyeDiagramBuilder.Build(trace, SampleRate, BitPeriod, skipBits: 0);

        int firstBinTotal = 0;
        for (int t = 0; t < histogram.TimeBinCount; t++)
            firstBinTotal += histogram.Counts[t, 0];
        firstBinTotal.ShouldBe(200);
    }

    [Fact]
    public void Build_EmptyTrace_Throws()
    {
        Should.Throw<ArgumentException>(
            () => EyeDiagramBuilder.Build(Array.Empty<double>(), SampleRate, BitPeriod));
    }

    [Fact]
    public void ToCsv_UsesInvariantCulture_RegardlessOfThreadCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var trace = AlternatingNrzTrace(bitCount: 32, amplitude: 1.5);
            var histogram = EyeDiagramBuilder.Build(trace, SampleRate, BitPeriod);

            var csv = histogram.ToCsv();

            csv.ShouldStartWith("time_s");
            csv.ShouldNotContain(";");
            // German decimal comma would corrupt the comma-separated layout:
            // every row must have exactly AmplitudeBinCount + 1 columns.
            var firstDataRow = csv.Split('\n')[1].TrimEnd('\r');
            firstDataRow.Split(',').Length.ShouldBe(histogram.AmplitudeBinCount + 1);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
