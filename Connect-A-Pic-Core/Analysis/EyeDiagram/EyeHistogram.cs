using System.Globalization;
using System.Text;

namespace CAP_Core.Analysis.EyeDiagram;

/// <summary>
/// 2D persistence histogram of an eye diagram: sample counts binned by time
/// offset within the bit period (rows) and by amplitude (columns).
/// </summary>
public class EyeHistogram
{
    /// <summary>Counts[timeBin, amplitudeBin] — number of trace samples falling into that cell.</summary>
    public int[,] Counts { get; }

    /// <summary>Number of time bins spanning one bit period.</summary>
    public int TimeBinCount => Counts.GetLength(0);

    /// <summary>Number of amplitude bins spanning [<see cref="MinAmplitude"/>, <see cref="MaxAmplitude"/>].</summary>
    public int AmplitudeBinCount => Counts.GetLength(1);

    /// <summary>Bit period in seconds (width of the eye window).</summary>
    public double BitPeriodSeconds { get; }

    /// <summary>Lower edge of the amplitude axis.</summary>
    public double MinAmplitude { get; }

    /// <summary>Upper edge of the amplitude axis.</summary>
    public double MaxAmplitude { get; }

    /// <summary>Initializes a new instance of <see cref="EyeHistogram"/>.</summary>
    /// <param name="counts">Pre-filled count grid [timeBin, amplitudeBin].</param>
    /// <param name="bitPeriodSeconds">Bit period in seconds.</param>
    /// <param name="minAmplitude">Lower amplitude edge.</param>
    /// <param name="maxAmplitude">Upper amplitude edge.</param>
    public EyeHistogram(int[,] counts, double bitPeriodSeconds, double minAmplitude, double maxAmplitude)
    {
        Counts = counts ?? throw new ArgumentNullException(nameof(counts));
        BitPeriodSeconds = bitPeriodSeconds;
        MinAmplitude = minAmplitude;
        MaxAmplitude = maxAmplitude;
    }

    /// <summary>
    /// Serializes the histogram to CSV (invariant culture): one row per time bin,
    /// first column = time-bin centre in seconds, remaining columns = counts per
    /// amplitude bin. The header row lists the amplitude-bin centres.
    /// </summary>
    public string ToCsv()
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;

        sb.Append("time_s");
        for (int a = 0; a < AmplitudeBinCount; a++)
            sb.Append(',').Append(AmplitudeBinCenter(a).ToString("G9", inv));
        sb.AppendLine();

        for (int t = 0; t < TimeBinCount; t++)
        {
            sb.Append(TimeBinCenter(t).ToString("G9", inv));
            for (int a = 0; a < AmplitudeBinCount; a++)
                sb.Append(',').Append(Counts[t, a].ToString(inv));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Centre time (seconds within the bit period) of time bin <paramref name="timeBin"/>.</summary>
    /// <param name="timeBin">Time-bin index.</param>
    public double TimeBinCenter(int timeBin)
        => (timeBin + 0.5) * BitPeriodSeconds / TimeBinCount;

    /// <summary>Centre amplitude of amplitude bin <paramref name="amplitudeBin"/>.</summary>
    /// <param name="amplitudeBin">Amplitude-bin index.</param>
    public double AmplitudeBinCenter(int amplitudeBin)
        => MinAmplitude + (amplitudeBin + 0.5) * (MaxAmplitude - MinAmplitude) / AmplitudeBinCount;
}
