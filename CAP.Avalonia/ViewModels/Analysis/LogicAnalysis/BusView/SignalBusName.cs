using System.Globalization;

namespace CAP.Avalonia.ViewModels.Analysis.LogicAnalysis.BusView;

/// <summary>
/// Name-family detection for the Logic panel's bus view (issue #1068, NAND game rung 5):
/// a signal name matching <c>&lt;prefix&gt;&lt;index&gt;</c> — letters ending in a decimal
/// index, like the shipped adders' <c>A0</c>–<c>A3</c> — can group into a bus. Names that
/// carry a dot (<c>GATE.PIN</c> raw ids) or no trailing index (<c>Cin</c>) never group.
/// Index 0 is the least-significant bit.
/// </summary>
public static class SignalBusName
{
    /// <summary>Highest member index a bus accepts — bit 62 keeps every shift defined.</summary>
    public const int MaxIndex = 62;

    /// <summary>
    /// Splits a candidate name into its bus prefix and member index. Returns false when
    /// the name has no trailing decimal index, an empty prefix, a dot anywhere
    /// (a raw <c>&lt;gate&gt;.&lt;pin&gt;</c> id must never look like a bus member), or
    /// an index above <see cref="MaxIndex"/>.
    /// </summary>
    public static bool TrySplit(string name, out string prefix, out int index)
    {
        prefix = "";
        index = 0;
        if (string.IsNullOrEmpty(name) || name.Contains('.'))
            return false;
        var splitAt = name.Length;
        while (splitAt > 0 && char.IsDigit(name[splitAt - 1]))
            splitAt--;
        if (splitAt == name.Length || splitAt == 0)
            return false;
        if (!int.TryParse(
                name[splitAt..], NumberStyles.None, CultureInfo.InvariantCulture, out index))
            return false;
        if (index > MaxIndex)
            return false;
        prefix = name[..splitAt];
        return true;
    }
}
