namespace CAP_Core.Solvers.Fdtd;

/// <summary>
/// Selects which FDTD S-matrix backend computes a component's scattering matrix.
/// The choice is persisted in user preferences and shared by every flow that
/// recomputes S-matrices (component settings today, the planned new-component
/// flow later).
/// </summary>
public enum FdtdBackendType
{
    /// <summary>
    /// Open-source Meep FDTD run locally in a pinned Docker image.
    /// Free, but slow and requires a running Docker engine.
    /// </summary>
    MeepDocker,

    /// <summary>
    /// Tidy3D cloud FDTD (Flexcompute). Dramatically faster than local Meep and
    /// needs no local compute, but each run costs credits and requires the
    /// <c>tidy3d</c> pip package plus an API key.
    /// </summary>
    Tidy3D,
}
