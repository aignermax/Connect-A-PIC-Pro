using System.Globalization;
using System.IO;
using CAP_Core.ComponentRegistry.RegistryClient;
using CAP_Core.Components.PinKinds;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.Services.ComponentRegistry;

/// <summary>
/// Maps a registry component manifest plus one of its S-parameter spectra into
/// a persistable <see cref="PdkComponentDraft"/> for the local "Registry" PDK
/// (issue #773). Physics rules: the draft's S-matrix is built EXCLUSIVELY from
/// the downloaded re/im samples — a spectrum that is empty, or whose traces
/// reference ports the manifest does not declare, aborts with
/// <see cref="InvalidDataException"/> instead of fabricating placeholder
/// values. The registry publishes no port coordinates, so the pin layout is a
/// deterministic visual convention (inputs left / outputs right), never a
/// physics claim; geometry-dependent fields (Nazca export, gdsfactory) stay
/// null, mirroring black-box GDS imports.
/// </summary>
public static class RegistryComponentDraftMapper
{
    /// <summary>Library category registry-downloaded components are grouped under.</summary>
    public const string RegistryCategory = "Registry";

    // Visual layout convention of the synthesized pin arrangement (µm).
    private const double ComponentWidthUm = 20.0;
    private const double VerticalPitchUm = 5.0;
    private const double EdgeMarginPitches = 1.0;
    private const double LeftEdgeAngleDegrees = 180.0;
    private const double RightEdgeAngleDegrees = 0.0;
    private const double UmToNm = 1000.0;
    private const double ZeroMagnitudeTolerance = 1e-12;

    /// <summary>
    /// Builds the local draft for <paramref name="manifest"/> from
    /// <paramref name="spectrum"/> (the downloaded artifact data).
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The spectrum is null, has no wavelength samples, or contains not a
    /// single trace whose endpoints are both declared manifest ports.
    /// </exception>
    public static PdkComponentDraft ToDraft(
        ComponentManifest manifest, ArtifactRef artifact, string tier, SParameterSpectrum spectrum)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(artifact);
        if (spectrum is null || spectrum.WavelengthUm.Count == 0)
        {
            throw new InvalidDataException(
                $"Registry artifact '{artifact.File}' of '{manifest.Id}' contains no wavelength data " +
                "— refusing to adopt it (no synthetic placeholder S-matrix is created).");
        }

        var portNames = manifest.Ports.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var validTraces = spectrum.S
            .Where(t => portNames.Contains(t.From) && portNames.Contains(t.To))
            .ToList();
        if (validTraces.Count == 0)
        {
            throw new InvalidDataException(
                $"Registry artifact '{artifact.File}' of '{manifest.Id}' has no S-parameter trace " +
                "between declared ports — refusing to adopt it (no synthetic placeholder S-matrix is created).");
        }

        return new PdkComponentDraft
        {
            Name = manifest.Name,
            Category = RegistryCategory,
            NazcaFunction = null!,
            WidthMicrometers = ComponentWidthUm,
            HeightMicrometers = ComputeHeightUm(manifest.Ports.Count),
            Pins = BuildPins(manifest),
            SMatrix = new PdkSMatrixDraft
            {
                WavelengthNm = ToNm(spectrum.WavelengthUm[0]),
                Connections = BuildConnections(validTraces, sampleIndex: 0),
                WavelengthData = BuildWavelengthData(spectrum, validTraces),
                SourceNote = BuildSourceNote(manifest, artifact, tier),
                SourceTimestampUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            },
        };
    }

    private static double ComputeHeightUm(int portCount)
    {
        int maxPerSide = Math.Max(1, (int)Math.Ceiling(Math.Max(1, portCount) / 2.0));
        return (maxPerSide + EdgeMarginPitches) * VerticalPitchUm;
    }

    /// <summary>
    /// Lays out the declared ports without inventing data: the first half on
    /// the left edge, the rest on the right edge, evenly spaced vertically.
    /// </summary>
    private static List<PhysicalPinDraft> BuildPins(ComponentManifest manifest)
    {
        var pins = new List<PhysicalPinDraft>(manifest.Ports.Count);
        int leftCount = (int)Math.Ceiling(manifest.Ports.Count / 2.0);
        for (int i = 0; i < manifest.Ports.Count; i++)
        {
            bool onLeft = i < leftCount;
            int row = onLeft ? i : i - leftCount;
            pins.Add(new PhysicalPinDraft
            {
                Name = manifest.Ports[i].Name,
                OffsetXMicrometers = onLeft ? 0.0 : ComponentWidthUm,
                OffsetYMicrometers = (row + EdgeMarginPitches) * VerticalPitchUm,
                AngleDegrees = onLeft ? LeftEdgeAngleDegrees : RightEdgeAngleDegrees,
                PinKind = PinKindHelper.OpticalKindName,
            });
        }
        return pins;
    }

    private static List<WavelengthSMatrixEntry> BuildWavelengthData(
        SParameterSpectrum spectrum, List<SParameterTrace> validTraces)
    {
        var entries = new List<WavelengthSMatrixEntry>(spectrum.WavelengthUm.Count);
        for (int i = 0; i < spectrum.WavelengthUm.Count; i++)
        {
            entries.Add(new WavelengthSMatrixEntry
            {
                WavelengthNm = ToNm(spectrum.WavelengthUm[i]),
                Connections = BuildConnections(validTraces, i),
            });
        }
        return entries;
    }

    /// <summary>
    /// Converts the re/im samples of every valid trace at
    /// <paramref name="sampleIndex"/> to polar form. Samples beyond a trace's
    /// array bounds and zero-magnitude connections are skipped — the PDK
    /// format lists only non-zero connections.
    /// </summary>
    private static List<SMatrixConnection> BuildConnections(
        List<SParameterTrace> validTraces, int sampleIndex)
    {
        var connections = new List<SMatrixConnection>(validTraces.Count);
        foreach (var trace in validTraces)
        {
            if (sampleIndex >= trace.Re.Count || sampleIndex >= trace.Im.Count)
                continue;

            double re = trace.Re[sampleIndex];
            double im = trace.Im[sampleIndex];
            double magnitude = Math.Sqrt(re * re + im * im);
            if (magnitude < ZeroMagnitudeTolerance)
                continue;

            connections.Add(new SMatrixConnection
            {
                FromPin = trace.From,
                ToPin = trace.To,
                Magnitude = magnitude,
                PhaseDegrees = Math.Atan2(im, re) * 180.0 / Math.PI,
            });
        }
        return connections;
    }

    /// <summary>
    /// One-line provenance for the Component Settings "source" display:
    /// registry id, tier, method/tool/author/date/fab of the artifact and the
    /// registry license — only parts that are actually present.
    /// </summary>
    private static string BuildSourceNote(ComponentManifest manifest, ArtifactRef artifact, string tier)
    {
        var provenance = artifact.Provenance;
        var parts = new List<string> { tier };
        if (!string.IsNullOrEmpty(provenance.Method))
            parts.Add(provenance.Method);
        if (!string.IsNullOrEmpty(provenance.Tool))
            parts.Add(provenance.Tool);
        if (!string.IsNullOrEmpty(provenance.CreatedBy))
            parts.Add($"by {provenance.CreatedBy}");
        if (!string.IsNullOrEmpty(provenance.Date))
            parts.Add(provenance.Date);
        if (!string.IsNullOrEmpty(provenance.Fab))
            parts.Add($"fab {provenance.Fab}");

        return $"Registry: {manifest.Id} ({string.Join(", ", parts)})" +
            (string.IsNullOrEmpty(manifest.License) ? "" : $" — license {manifest.License}");
    }

    private static int ToNm(double wavelengthUm) =>
        (int)Math.Round(wavelengthUm * UmToNm, MidpointRounding.AwayFromZero);
}
