namespace CAP_Core.Solvers.Fdtd;

/// <summary>
/// Sweep settings for an FDTD run, planned so the uniform wavelength grid hits
/// the component's already-defined wavelengths exactly (issue #582 — otherwise
/// the recompute overwrites only its own range and leaves e.g. 980/1310 nm
/// stale, producing a mixed effective S-matrix).
/// </summary>
/// <param name="StartUm">Sweep start wavelength in µm.</param>
/// <param name="StopUm">Sweep stop wavelength in µm.</param>
/// <param name="Points">Number of uniformly spaced wavelength points.</param>
/// <param name="UncoveredNm">
/// Existing wavelengths (nm) the planned grid cannot hit exactly (only when the
/// exact grid would exceed <see cref="FdtdWavelengthPlanner.MaxPoints"/>).
/// Callers should surface these so nothing stale remains silently.
/// </param>
public sealed record FdtdWavelengthPlan(
    double StartUm, double StopUm, int Points, IReadOnlyList<int> UncoveredNm);

/// <summary>
/// Plans the FDTD wavelength sweep from a component's already-defined
/// wavelengths so the recompute overwrites every existing S-matrix entry
/// instead of leaving out-of-range ones stale.
/// </summary>
public static class FdtdWavelengthPlanner
{
    /// <summary>Fallback sweep start (µm) when the component defines no wavelengths.</summary>
    public const double DefaultStartUm = 1.5;

    /// <summary>Fallback sweep stop (µm) when the component defines no wavelengths.</summary>
    public const double DefaultStopUm = 1.6;

    /// <summary>Fallback point count when the component defines no wavelengths.</summary>
    public const int DefaultPoints = 11;

    /// <summary>
    /// Upper bound on sweep points — each point costs solver time, so a
    /// pathological wavelength set (huge span, coprime spacings) must not
    /// explode into thousands of points.
    /// </summary>
    public const int MaxPoints = 41;

    /// <summary>Half-width (nm) of the band swept around a single defined wavelength.</summary>
    public const int SingleWavelengthHalfBandNm = 50;

    /// <summary>Points for the single-wavelength band; odd so the centre point hits it exactly.</summary>
    public const int SingleWavelengthPoints = 11;

    private const double NmPerUm = 1000.0;

    /// <summary>
    /// Plans a sweep covering <paramref name="existingWavelengthsNm"/> exactly.
    /// With no existing wavelengths the default 1.5–1.6 µm sweep is returned;
    /// with one, a band centred on it; with several, the coarsest uniform grid
    /// through all of them (their GCD spacing), capped at <see cref="MaxPoints"/> —
    /// beyond the cap, unhit wavelengths are reported as uncovered.
    /// </summary>
    /// <param name="existingWavelengthsNm">Wavelengths (nm) the component's S-matrix already defines.</param>
    public static FdtdWavelengthPlan Plan(IEnumerable<int> existingWavelengthsNm)
    {
        var sorted = (existingWavelengthsNm ?? Array.Empty<int>())
            .Where(nm => nm > 0)
            .Distinct()
            .OrderBy(nm => nm)
            .ToList();

        if (sorted.Count == 0)
            return new FdtdWavelengthPlan(DefaultStartUm, DefaultStopUm, DefaultPoints, Array.Empty<int>());

        if (sorted.Count == 1)
            return PlanSingle(sorted[0]);

        return PlanMulti(sorted);
    }

    private static FdtdWavelengthPlan PlanSingle(int wavelengthNm)
    {
        // Odd point count over a symmetric band → the centre grid point is the
        // defined wavelength itself, so it gets overwritten exactly.
        double startUm = (wavelengthNm - SingleWavelengthHalfBandNm) / NmPerUm;
        double stopUm = (wavelengthNm + SingleWavelengthHalfBandNm) / NmPerUm;
        return new FdtdWavelengthPlan(startUm, stopUm, SingleWavelengthPoints, Array.Empty<int>());
    }

    private static FdtdWavelengthPlan PlanMulti(IReadOnlyList<int> sorted)
    {
        int min = sorted[0];
        int max = sorted[^1];

        // A uniform grid from min hits every wavelength iff its step divides all
        // offsets (wl - min); the GCD is the largest such step → fewest points.
        int stepNm = sorted.Skip(1).Aggregate(0, (g, wl) => Gcd(g, wl - min));
        int points = (max - min) / stepNm + 1;

        if (points <= MaxPoints)
            return new FdtdWavelengthPlan(min / NmPerUm, max / NmPerUm, points, Array.Empty<int>());

        // Exact coverage would need too many points: sweep the span with the cap
        // and report every wavelength the capped grid misses, so the caller can
        // warn that those entries stay stale.
        double cappedStep = (max - min) / (double)(MaxPoints - 1);
        var uncovered = sorted
            .Where(wl => !IsOnGrid(wl, min, cappedStep))
            .ToList();
        return new FdtdWavelengthPlan(min / NmPerUm, max / NmPerUm, MaxPoints, uncovered);
    }

    private static bool IsOnGrid(int wavelengthNm, int minNm, double stepNm)
    {
        double index = (wavelengthNm - minNm) / stepNm;
        double nearest = Math.Round(index);
        // Result wavelengths get rounded to whole nm downstream, so "hit" means
        // the nearest grid point lands within half a nanometre.
        return Math.Abs(minNm + nearest * stepNm - wavelengthNm) < 0.5;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a;
    }
}
