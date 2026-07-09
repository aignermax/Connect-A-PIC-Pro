using System;
using System.Collections.Generic;
using System.Globalization;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Persistence.PIR;

namespace CAP.Avalonia.Services.AddCustomComponent;

/// <summary>
/// Converts an FDTD-computed <see cref="ComponentSMatrixData"/> into the PDK draft's
/// <see cref="PdkSMatrixDraft"/>, and provides the two honest no-FDTD fallbacks: a
/// black-box (no simulation model) and a lossless two-port pass-through for pure
/// routing components. Never fabricates S-parameter values — every number in the
/// resulting draft is either copied verbatim from the FDTD result (converted from
/// rectangular to polar form, which is pure math, not physics) or is the exact
/// lossless identity (magnitude 1, phase 0).
/// </summary>
public static class FdtdSMatrixToDraftConverter
{
    /// <summary>
    /// Builds a multi-wavelength <see cref="PdkSMatrixDraft"/> from a raw FDTD
    /// <see cref="ComponentSMatrixData"/> result. Every row-major (Real, Imag) entry of
    /// every wavelength is converted to a polar-form <see cref="SMatrixConnection"/>
    /// between the corresponding ports, with no filtering or interpretation of the
    /// FDTD values. Returns null when <paramref name="data"/> has no wavelength
    /// entries, since a draft without any S-matrix content is meaningless.
    /// </summary>
    /// <param name="data">The FDTD-computed S-matrix data, keyed by wavelength in nm.</param>
    public static PdkSMatrixDraft? FromFdtd(ComponentSMatrixData data)
    {
        if (data.Wavelengths == null || data.Wavelengths.Count == 0)
            return null;

        var entries = new List<WavelengthSMatrixEntry>();
        foreach (var pair in data.Wavelengths)
        {
            int wavelengthNm = int.Parse(pair.Key, CultureInfo.InvariantCulture);
            entries.Add(new WavelengthSMatrixEntry
            {
                WavelengthNm = wavelengthNm,
                Connections = ToConnections(pair.Value)
            });
        }

        return new PdkSMatrixDraft
        {
            WavelengthNm = entries[0].WavelengthNm,
            WavelengthData = entries
        };
    }

    /// <summary>No FDTD model available — the component stays a black box (no S-matrix draft).</summary>
    public static PdkSMatrixDraft? BlackBox() => null;

    /// <summary>
    /// The honest lossless two-port pass-through: unit transmission magnitude and zero
    /// phase in both directions. Used only for pure 2-port routing components where the
    /// ideal is physically exact (not assumed), so a full FDTD run would add no information.
    /// </summary>
    /// <param name="inPin">Name of the first port.</param>
    /// <param name="outPin">Name of the second port.</param>
    /// <param name="wavelengthNm">Wavelength in nm this ideal applies at.</param>
    public static PdkSMatrixDraft LosslessTwoPort(string inPin, string outPin, int wavelengthNm) => new()
    {
        WavelengthNm = wavelengthNm,
        Connections = new()
        {
            new SMatrixConnection { FromPin = inPin, ToPin = outPin, Magnitude = 1.0, PhaseDegrees = 0.0 },
            new SMatrixConnection { FromPin = outPin, ToPin = inPin, Magnitude = 1.0, PhaseDegrees = 0.0 },
        }
    };

    /// <summary>
    /// Converts a single row-major (Real, Imag) S-matrix into port-to-port polar-form
    /// connections. Row index is the output port, column index is the input port
    /// (standard S-parameter convention S[out, in]). Port names come from
    /// <see cref="SMatrixWavelengthEntry.PortNames"/>; when absent, ports are named by
    /// their zero-based matrix index.
    /// </summary>
    private static List<SMatrixConnection> ToConnections(SMatrixWavelengthEntry matrix)
    {
        var portNames = matrix.PortNames ?? IndexPortNames(matrix.Rows);
        var connections = new List<SMatrixConnection>(matrix.Real.Count);

        for (int row = 0; row < matrix.Rows; row++)
        {
            for (int col = 0; col < matrix.Cols; col++)
            {
                int index = row * matrix.Cols + col;
                double real = matrix.Real[index];
                double imag = matrix.Imag[index];

                connections.Add(new SMatrixConnection
                {
                    FromPin = portNames[col],
                    ToPin = portNames[row],
                    Magnitude = Math.Sqrt(real * real + imag * imag),
                    PhaseDegrees = Math.Atan2(imag, real) * (180.0 / Math.PI)
                });
            }
        }

        return connections;
    }

    /// <summary>Fallback port names ("0", "1", ...) used when the FDTD result has no port names.</summary>
    private static List<string> IndexPortNames(int portCount)
    {
        var names = new List<string>(portCount);
        for (int i = 0; i < portCount; i++)
            names.Add(i.ToString(CultureInfo.InvariantCulture));
        return names;
    }
}
