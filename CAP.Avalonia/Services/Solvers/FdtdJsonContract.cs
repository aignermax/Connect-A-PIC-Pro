using System.Numerics;
using System.Text.Json;
using CAP_Core.Solvers.Fdtd;

namespace CAP.Avalonia.Services.Solvers;

/// <summary>
/// Translates between <see cref="FdtdSMatrixRequest"/>/<see cref="FdtdSMatrixResult"/>
/// and the JSON contract of <c>scripts/fdtd_sparams.py</c>. Kept separate from the
/// Docker driver so the contract can be unit-tested without spawning a container.
/// </summary>
public static class FdtdJsonContract
{
    /// <summary>
    /// Serialises a request to the script's stdin/spec JSON. <paramref name="gdsPathInContainer"/>
    /// is the GDS path as seen inside the container (the host file is volume-mounted).
    /// </summary>
    public static string SerialiseRequest(FdtdSMatrixRequest req, string gdsPathInContainer)
    {
        var obj = new
        {
            gds_path = gdsPathInContainer,
            polygons = req.Polygons.Select(poly => new
            {
                layer = poly.Layer,
                points = poly.Points.Select(pt => new[] { pt.X, pt.Y }),
            }),
            ports = req.Ports.Select(p => new
            {
                name = p.Name,
                x = p.X,
                y = p.Y,
                orientation = p.Orientation,
                width = p.Width,
            }),
            layer = new[] { req.LayerNumber, req.LayerDatatype },
            wavelength_start = req.WavelengthStart,
            wavelength_stop = req.WavelengthStop,
            wavelength_points = req.WavelengthPoints,
            resolution = req.Resolution,
            is_3d = req.Is3D,
            ymargin = req.YMargin,
            xmargin = req.XMargin,
        };
        return JsonSerializer.Serialize(obj);
    }

    /// <summary>
    /// Parses the JSON the script writes on stdout. Robust against log chatter
    /// before the result. Surfaces solver failures with their raw stderr.
    /// </summary>
    public static FdtdSMatrixResult ParseOutput(string stdout, string stderr = "")
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return FdtdSMatrixResult.Fail("FDTD solver produced no output.", rawStderr: stderr);

        var jsonLine = SubprocessJsonRunner.ExtractTrailingJsonLine(stdout);
        if (jsonLine == null)
            return FdtdSMatrixResult.Fail(
                $"No JSON found in FDTD solver output: {Truncate(stdout, 300)}", rawStderr: stderr);

        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;

            if (root.TryGetProperty("success", out var sp) && !sp.GetBoolean())
            {
                var error = root.TryGetProperty("error", out var ep) ? ep.GetString() : null;
                var missing = root.TryGetProperty("missing_backend", out var mb) ? mb.GetString() : null;
                // The bridges attach the python traceback as "trace" on failure — append it
                // to the diagnostics so cloud-side failures stay debuggable from the UI.
                var trace = root.TryGetProperty("trace", out var tp) ? tp.GetString() : null;
                var diagnostics = string.IsNullOrWhiteSpace(trace) ? stderr
                    : string.IsNullOrWhiteSpace(stderr) ? trace
                    : stderr + "\n" + trace;
                return FdtdSMatrixResult.Fail(error ?? "Unknown FDTD solver error", diagnostics, missing);
            }

            return new FdtdSMatrixResult
            {
                Success = true,
                Is3D = root.TryGetProperty("is_3d", out var td) && td.GetBoolean(),
                Ports = ReadStringArray(root, "ports"),
                Wavelengths = ReadDoubleArray(root, "wavelengths"),
                Entries = ReadEntries(root),
                EnergySumPerInput = ReadEnergy(root),
            };
        }
        catch (Exception ex)
        {
            return FdtdSMatrixResult.Fail($"Failed to parse FDTD output: {ex.Message}", rawStderr: stderr);
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        var list = new List<string>();
        if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var v in arr.EnumerateArray())
                if (v.GetString() is { } s) list.Add(s);
        return list;
    }

    private static IReadOnlyList<double> ReadDoubleArray(JsonElement root, string name)
    {
        var list = new List<double>();
        if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var v in arr.EnumerateArray()) list.Add(v.GetDouble());
        return list;
    }

    private static IReadOnlyList<FdtdSEntry> ReadEntries(JsonElement root)
    {
        var list = new List<FdtdSEntry>();
        if (!root.TryGetProperty("s", out var s) || s.ValueKind != JsonValueKind.Object)
            return list;

        foreach (var prop in s.EnumerateObject())
        {
            var values = new List<Complex>();
            foreach (var pair in prop.Value.EnumerateArray())
            {
                values.Add(new Complex(pair[0].GetDouble(), pair[1].GetDouble()));
            }
            list.Add(new FdtdSEntry { Key = prop.Name, Values = values });
        }
        return list;
    }

    private static IReadOnlyDictionary<string, double> ReadEnergy(JsonElement root)
    {
        var dict = new Dictionary<string, double>();
        if (root.TryGetProperty("energy_sum_per_input", out var e) && e.ValueKind == JsonValueKind.Object)
            foreach (var prop in e.EnumerateObject()) dict[prop.Name] = prop.Value.GetDouble();
        return dict;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
