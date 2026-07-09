using System.Collections.Generic;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Persistence.PIR;

namespace CAP.Avalonia.Services.AddCustomComponent;

/// <summary>
/// The honest S-matrix choices offered by the New Component window (issue #701). There is
/// deliberately no "guess a matrix" option — every source is either real FDTD data, the
/// absence of a model, or a physically exact ideal.
/// </summary>
public enum SMatrixSource
{
    /// <summary>No simulation model — the component is saved as a black box.</summary>
    BlackBox,

    /// <summary>Use the S-matrix computed by the FDTD solver in this session.</summary>
    Fdtd,

    /// <summary>
    /// The lossless two-port pass-through ideal: unit magnitude, zero phase, both
    /// directions. Only valid for pure 2-port routing geometry, where the ideal is
    /// physically exact rather than an assumption.
    /// </summary>
    LosslessTwoPort,
}

/// <summary>
/// Outcome of resolving an <see cref="SMatrixSource"/> choice against the session state:
/// either a draft to save (null draft = black box) or an error explaining why the choice
/// is not applicable right now.
/// </summary>
/// <param name="Success">Whether the choice resolved to a savable S-matrix (or black box).</param>
/// <param name="Draft">The S-matrix draft to attach; null means black box.</param>
/// <param name="Error">Reason the choice could not be resolved, when <see cref="Success"/> is false.</param>
public sealed record SMatrixResolution(bool Success, PdkSMatrixDraft? Draft, string? Error)
{
    /// <summary>A successful resolution carrying <paramref name="draft"/> (null = black box).</summary>
    public static SMatrixResolution Ok(PdkSMatrixDraft? draft) => new(true, draft, null);

    /// <summary>A failed resolution with a user-facing reason.</summary>
    public static SMatrixResolution Fail(string error) => new(false, null, error);
}

/// <summary>
/// Validates and materializes the user's <see cref="SMatrixSource"/> choice into a
/// <see cref="PdkSMatrixDraft"/>. Kept outside the view model so the "no invented physics"
/// gating (FDTD requires a computed result; the lossless ideal requires exactly two ports)
/// is a small, directly testable unit.
/// </summary>
public static class SMatrixSourceResolver
{
    /// <summary>
    /// The wavelength the lossless two-port ideal is recorded at. The ideal is
    /// wavelength-independent (unit magnitude, zero phase at every wavelength), so this is
    /// only the draft's bookkeeping anchor; C-band 1550 nm matches the app-wide default.
    /// </summary>
    public const int LosslessIdealWavelengthNm = 1550;

    /// <summary>Exactly this many ports are required for the lossless pass-through ideal.</summary>
    private const int LosslessIdealPortCount = 2;

    /// <summary>
    /// Resolves <paramref name="source"/> using the session's computed FDTD model and the
    /// rendered preview pins. Never fabricates values: FDTD without a computed result and
    /// the lossless ideal on a non-2-port geometry both fail with an explanation instead
    /// of degrading silently.
    /// </summary>
    /// <param name="source">The user's S-matrix choice.</param>
    /// <param name="computedModel">The FDTD result computed this session, if any.</param>
    /// <param name="pins">Pins extracted from the rendered preview.</param>
    public static SMatrixResolution Resolve(
        SMatrixSource source, ComponentSMatrixData? computedModel, IReadOnlyList<OverridePinData> pins)
    {
        switch (source)
        {
            case SMatrixSource.Fdtd when computedModel is null:
                return SMatrixResolution.Fail(
                    "No FDTD result available — run 'Compute S-Matrix' first, or choose black box.");
            case SMatrixSource.Fdtd:
                return SMatrixResolution.Ok(FdtdSMatrixToDraftConverter.FromFdtd(computedModel));
            case SMatrixSource.LosslessTwoPort when pins.Count != LosslessIdealPortCount:
                return SMatrixResolution.Fail(
                    $"The lossless pass-through ideal requires exactly {LosslessIdealPortCount} ports, " +
                    $"but the preview has {pins.Count}. Use FDTD or black box instead.");
            case SMatrixSource.LosslessTwoPort:
                return SMatrixResolution.Ok(FdtdSMatrixToDraftConverter.LosslessTwoPort(
                    pins[0].Name, pins[1].Name, LosslessIdealWavelengthNm));
            default:
                return SMatrixResolution.Ok(FdtdSMatrixToDraftConverter.BlackBox());
        }
    }
}
