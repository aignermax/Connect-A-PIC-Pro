namespace CAP_Core.Analysis.EyeDiagram;

/// <summary>
/// Standard PRBS (pseudo-random binary sequence) polynomial orders per ITU-T O.150.
/// The enum value equals the shift-register length n; the pattern repeats every 2^n − 1 bits.
/// </summary>
public enum PrbsOrder
{
    /// <summary>x⁷ + x⁶ + 1 — 127-bit pattern.</summary>
    Prbs7 = 7,

    /// <summary>x¹¹ + x⁹ + 1 — 2047-bit pattern.</summary>
    Prbs11 = 11,

    /// <summary>x²³ + x¹⁸ + 1 — 8 388 607-bit pattern.</summary>
    Prbs23 = 23,
}

/// <summary>
/// Generates PRBS bit patterns via a Fibonacci LFSR and expands them into
/// NRZ (non-return-to-zero) sample streams for transient simulation.
/// </summary>
public static class PrbsGenerator
{
    /// <summary>Second feedback tap (first tap is the register length itself).</summary>
    private const int Prbs7SecondTap = 6;
    private const int Prbs11SecondTap = 9;
    private const int Prbs23SecondTap = 18;

    /// <summary>Full pattern length 2^n − 1 for the given order.</summary>
    /// <param name="order">PRBS order.</param>
    public static int PatternLength(PrbsOrder order) => (1 << (int)order) - 1;

    /// <summary>
    /// Generates the first <paramref name="bitCount"/> bits of the PRBS pattern.
    /// The LFSR is seeded with all ones, so the sequence is deterministic.
    /// </summary>
    /// <param name="order">PRBS order (register length and taps).</param>
    /// <param name="bitCount">Number of bits to emit (may be less than the full pattern length).</param>
    public static bool[] GenerateBits(PrbsOrder order, int bitCount)
    {
        if (bitCount <= 0) throw new ArgumentOutOfRangeException(nameof(bitCount));

        int length = (int)order;
        int secondTap = order switch
        {
            PrbsOrder.Prbs7 => Prbs7SecondTap,
            PrbsOrder.Prbs11 => Prbs11SecondTap,
            PrbsOrder.Prbs23 => Prbs23SecondTap,
            _ => throw new ArgumentOutOfRangeException(nameof(order)),
        };

        // Fibonacci LFSR: bit i of `state` holds register stage i+1; seed = all ones.
        // feedback = stage(length) XOR stage(secondTap); new bit shifts in at stage 1.
        uint mask = (1u << length) - 1;
        uint state = mask;
        var bits = new bool[bitCount];
        for (int i = 0; i < bitCount; i++)
        {
            bits[i] = (state & 1) == 1;
            uint feedback = ((state >> (length - 1)) ^ (state >> (secondTap - 1))) & 1;
            state = ((state << 1) | feedback) & mask;
        }
        return bits;
    }

    /// <summary>
    /// Expands a bit pattern into an NRZ sample stream: each bit is held constant
    /// for <paramref name="samplesPerBit"/> samples at <paramref name="amplitude"/> (one) or 0 (zero).
    /// </summary>
    /// <param name="bits">Bit pattern to expand.</param>
    /// <param name="samplesPerBit">Samples per bit period (≥ 1).</param>
    /// <param name="amplitude">Sample value representing a logical one.</param>
    public static double[] ToNrzSamples(bool[] bits, int samplesPerBit, double amplitude)
    {
        if (bits == null) throw new ArgumentNullException(nameof(bits));
        if (samplesPerBit < 1) throw new ArgumentOutOfRangeException(nameof(samplesPerBit));

        var samples = new double[bits.Length * samplesPerBit];
        for (int bit = 0; bit < bits.Length; bit++)
        {
            if (!bits[bit]) continue;
            int start = bit * samplesPerBit;
            for (int s = 0; s < samplesPerBit; s++)
                samples[start + s] = amplitude;
        }
        return samples;
    }
}
