using System.Text.Json;
using System.Text.Json.Nodes;
using CAP_Core.Solvers.Fdtd;

namespace CAP.Avalonia.Services.Solvers;

// JSON contract of scripts/tidy3d_sparams.py: the solve-result JSON is identical
// to the Meep bridge (parsed by FdtdJsonContract.ParseOutput); this class only adds
// the Tidy3D-specific "mode" field and the check/estimate result parsers.
public static class Tidy3dJsonContract
{
    public const string ModeCheck = "check";

    public const string ModeEstimate = "estimate";

    public const string ModeSolve = "solve";

    // Unlike the Docker bridge there is no container path mapping — the GDS path
    // is the host path.
    public static string SerialiseRequest(FdtdSMatrixRequest request, string mode)
    {
        var node = JsonNode.Parse(FdtdJsonContract.SerialiseRequest(request, request.GdsPath))!;
        node["mode"] = mode;
        return node.ToJsonString();
    }

    public static FdtdAvailability ParseCheck(string stdout, string stderr = "")
    {
        var jsonLine = SubprocessJsonRunner.ExtractTrailingJsonLine(stdout);
        if (jsonLine == null)
            return FdtdAvailability.Unavailable(
                $"Tidy3D availability check produced no result. {FirstLine(stderr)}".Trim());

        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var sp) && sp.GetBoolean())
            {
                var version = root.TryGetProperty("tidy3d_version", out var v) ? v.GetString() : null;
                return FdtdAvailability.Available($"Tidy3D {version ?? "(unknown version)"} ready.");
            }

            var error = root.TryGetProperty("error", out var ep) ? ep.GetString() : null;
            return FdtdAvailability.Unavailable(error ?? "Tidy3D is not available.");
        }
        catch (JsonException ex)
        {
            return FdtdAvailability.Unavailable($"Could not parse Tidy3D check output: {ex.Message}");
        }
    }

    public static FdtdCostEstimate ParseEstimate(string stdout, string stderr = "")
    {
        var jsonLine = SubprocessJsonRunner.ExtractTrailingJsonLine(stdout);
        if (jsonLine == null)
            return FdtdCostEstimate.Fail(
                $"Tidy3D cost estimation produced no result. {FirstLine(stderr)}".Trim());

        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var sp) && sp.GetBoolean())
            {
                return new FdtdCostEstimate
                {
                    Success = true,
                    EstimatedCredits = root.TryGetProperty("estimated_credits", out var c) ? c.GetDouble() : 0,
                    SimulationCount = root.TryGetProperty("simulation_count", out var n) ? n.GetInt32() : 0,
                };
            }

            var error = root.TryGetProperty("error", out var ep) ? ep.GetString() : null;
            return FdtdCostEstimate.Fail(error ?? "Unknown Tidy3D estimation error.");
        }
        catch (JsonException ex)
        {
            return FdtdCostEstimate.Fail($"Could not parse Tidy3D estimate output: {ex.Message}");
        }
    }

    private static string FirstLine(string text)
    {
        var idx = text.IndexOf('\n');
        return (idx >= 0 ? text[..idx] : text).Trim();
    }
}
