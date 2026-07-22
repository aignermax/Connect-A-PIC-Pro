using System.Globalization;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;

namespace CAP.Avalonia.ViewModels.ComponentSettings;

/// <summary>
/// Read-only display row for the "Currently effective S-matrix" section in the
/// Component Settings dialog. Shows whatever S-matrix the simulator will use
/// for a given wavelength right now — i.e. the PDK default unless an override
/// has replaced it. Source-tagged so the user can tell which layer wins
/// without cross-referencing the override list.
/// </summary>
public class EffectiveSMatrixEntryViewModel
{
    /// <summary>Wavelength label (e.g. "1550 nm").</summary>
    public string WavelengthLabel { get; }

    /// <summary>Matrix dimensions string (e.g. "4 × 4").</summary>
    public string Dimensions { get; }

    /// <summary>
    /// Source-of-truth tag — "PDK Default" when no override is in play for this
    /// wavelength, "Override active" otherwise (with the override's provenance,
    /// e.g. "Override active — FDTD Meep 2D", when known). The dialog wraps the
    /// row in a faintly tinted background based on this tag so the visual
    /// difference is obvious at a glance.
    /// </summary>
    public string SourceTag { get; }

    /// <summary>True when <see cref="SourceTag"/> is "Override active".</summary>
    public bool IsOverridden { get; }

    /// <summary>
    /// Strongest off-diagonal couplings (top 4) as "P_i→P_j=value". Skips the
    /// diagonal because |S_ii| is the reflection at port i, which is ≈0 for
    /// the passive photonic devices in our PDKs and so makes a useless preview.
    /// </summary>
    public string MagnitudePreview { get; }

    /// <summary>
    /// Builds an entry from a live <see cref="SMatrix"/> and the component's
    /// physical pin list. Reads the pin-keyed transfer values via the
    /// SMatrix.PinReference index map; if any expected pin id is missing the
    /// preview falls back to the empty string rather than throwing — the
    /// dialog should never crash because of an unusual S-matrix shape.
    /// </summary>
    /// <param name="wavelengthNm">Wavelength of this row in nanometres.</param>
    /// <param name="sMatrix">The S-matrix effective at this wavelength.</param>
    /// <param name="pins">The component's logical pins, in display order.</param>
    /// <param name="isOverridden">True when an override supplies this wavelength.</param>
    /// <param name="overrideSourceNote">
    /// Provenance of the active override (e.g. "FDTD Meep 2D"); shown in the tag
    /// so the user can tell FDTD-computed rows from imported ones (#582).
    /// </param>
    public EffectiveSMatrixEntryViewModel(
        int wavelengthNm,
        SMatrix sMatrix,
        IReadOnlyList<Pin> pins,
        bool isOverridden,
        string? overrideSourceNote = null)
    {
        WavelengthLabel = $"{wavelengthNm} nm";
        Dimensions = $"{pins.Count} × {pins.Count}";
        IsOverridden = isOverridden;
        SourceTag = BuildSourceTag(isOverridden, overrideSourceNote);
        MagnitudePreview = BuildMagnitudePreview(sMatrix, pins);
    }

    private static string BuildSourceTag(bool isOverridden, string? overrideSourceNote)
    {
        if (!isOverridden)
            return "PDK Default";
        return string.IsNullOrWhiteSpace(overrideSourceNote)
            ? "Override active"
            : $"Override active — {overrideSourceNote}";
    }

    private static string BuildMagnitudePreview(SMatrix sMatrix, IReadOnlyList<Pin> pins)
    {
        if (pins.Count == 0)
            return string.Empty;

        var couplings = new List<(int From, int To, double Magnitude)>();

        // S[out, in] convention. We label pairs as "input → output" so the
        // user reads them as "Port 1 input couples 70% into Port 3 output" etc.
        for (int from = 0; from < pins.Count; from++)
        {
            if (!sMatrix.PinReference.TryGetValue(pins[from].IDInFlow, out var inIdx))
                continue;

            for (int to = 0; to < pins.Count; to++)
            {
                if (from == to) continue; // skip reflection
                if (!sMatrix.PinReference.TryGetValue(pins[to].IDOutFlow, out var outIdx))
                    continue;

                var v = sMatrix.SMat[outIdx, inIdx];
                double mag = Math.Sqrt(v.Real * v.Real + v.Imaginary * v.Imaginary);
                if (mag < 1e-6) continue; // hide noise-floor entries

                couplings.Add((from, to, mag));
            }
        }

        if (couplings.Count == 0)
            return "(no significant couplings)";

        couplings.Sort((a, b) => b.Magnitude.CompareTo(a.Magnitude));
        var top = couplings.Take(4)
            // InvariantCulture so "0.707" stays "0.707" on de-DE / fr-FR locales.
            .Select(c => $"P{c.From + 1}→P{c.To + 1}={c.Magnitude.ToString("F3", CultureInfo.InvariantCulture)}");

        var preview = string.Join("  ", top);
        return couplings.Count > 4 ? preview + " …" : preview;
    }
}
