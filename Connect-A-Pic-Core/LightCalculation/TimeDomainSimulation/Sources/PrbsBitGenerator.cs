namespace CAP_Core.LightCalculation.TimeDomainSimulation.Sources;

/// <summary>
/// Deterministic pseudo-random binary sequence generator using a Fibonacci
/// LFSR with maximal-length feedback polynomials (period 2^order − 1).
/// Used by <see cref="PrbsSource"/>; exposed for direct testing of the
/// sequence properties (period, mark/space balance, seed determinism).
/// </summary>
public static class PrbsBitGenerator
{
    /// <summary>
    /// Second feedback tap per supported PRBS order (the first tap is the
    /// order itself), from the standard maximal-length polynomials:
    /// x⁷+x⁶+1, x⁹+x⁵+1, x¹¹+x⁹+1, x¹⁵+x¹⁴+1, x²³+x¹⁸+1, x³¹+x²⁸+1.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, int> SecondTapByOrder =
        new Dictionary<int, int> { { 7, 6 }, { 9, 5 }, { 11, 9 }, { 15, 14 }, { 23, 18 }, { 31, 28 } };

    /// <summary>Orders with a registered maximal-length polynomial.</summary>
    public static IEnumerable<int> SupportedOrders => SecondTapByOrder.Keys.OrderBy(o => o);

    /// <summary>
    /// Generates <paramref name="count"/> PRBS bits.
    /// </summary>
    /// <param name="order">PRBS order (7, 9, 11, 15, 23 or 31).</param>
    /// <param name="seed">
    /// Initial LFSR state; only the low <paramref name="order"/> bits are used
    /// and an all-zero state is replaced by 1 (all-zero is a fixed point).
    /// The same seed always yields the same sequence.
    /// </param>
    /// <param name="count">Number of bits to generate (≥ 0).</param>
    public static bool[] Generate(int order, int seed, int count)
    {
        if (!SecondTapByOrder.TryGetValue(order, out int secondTap))
            throw new ArgumentException(
                $"Unsupported PRBS order {order}. Supported: {string.Join(", ", SupportedOrders)}.",
                nameof(order));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

        uint mask = order == 31 ? 0x7FFFFFFFu : (1u << order) - 1;
        uint state = (uint)seed & mask;
        if (state == 0) state = 1;

        var bits = new bool[count];
        for (int i = 0; i < count; i++)
        {
            uint feedback = ((state >> (order - 1)) ^ (state >> (secondTap - 1))) & 1u;
            bits[i] = feedback == 1u;
            state = ((state << 1) | feedback) & mask;
        }
        return bits;
    }
}
