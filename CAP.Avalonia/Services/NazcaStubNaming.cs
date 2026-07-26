using System.Security.Cryptography;
using System.Text;
using CAP_Core.Export;

namespace CAP.Avalonia.Services;

/// <summary>
/// Names the generated Nazca stub cells/functions. The stub generator dedupes by
/// function name, so two placements of the same PDK function with DIFFERENT parameters
/// would share one stub cell — the first placement's dimensions win, and the klayout
/// upgrade (<see cref="SiepicCellUpgradeWriter"/>) would resolve the losing variants
/// with the wrong parameters, rendering physically wrong silicon (issue #783).
/// Parameterized components therefore get a short deterministic parameters hash
/// appended, so each distinct parameter set gets its own cell. Unparameterized
/// components keep the bare function name — existing exports are unchanged.
/// The name must be identically computable at every site that names or references
/// the stub cell (stub generation, placement call sites, the klayout upgrade map),
/// which is why it lives in this one helper.
/// </summary>
internal static class NazcaStubNaming
{
    /// <summary>
    /// Stub cell/function name for one component. The hash is appended only when the
    /// placement actually calls the generated stub with parameters: dotted names
    /// (<c>demo.shallow.bend</c>) call the real module function directly (their stub
    /// is never invoked), and parametric straights embed the length in their runtime
    /// cell name already — both keep the bare name.
    /// </summary>
    public static string StubName(string funcName, string? parameters)
    {
        if (string.IsNullOrEmpty(parameters)
            || funcName.Contains('.', StringComparison.Ordinal)
            || NazcaCoordinateMapper.IsParametricStraight(funcName, parameters))
            return funcName;
        return $"{funcName}_{ParametersHash(parameters)}";
    }

    /// <summary>
    /// 6 lowercase hex chars of SHA-256 over the parameter string — deterministic
    /// across runs and platforms, and collision-safe for the handful of parameter
    /// sets one design places per function. Keeps GDS cell names short
    /// (<c>ebeam_dc_te1550_7f986c</c> is 22 chars; even &gt;32 would be safe).
    /// </summary>
    public static string ParametersHash(string parameters)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(parameters));
        return Convert.ToHexString(bytes, 0, 3).ToLowerInvariant();
    }
}
