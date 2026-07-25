namespace CAP_Core.Solvers.Fdtd;

public enum FdtdBackendType
{
    // Open-source Meep FDTD run locally in a pinned Docker image: free, but needs
    // a running Docker engine.
    MeepDocker,

    // Tidy3D cloud FDTD (Flexcompute): fast and needs no local compute, but each
    // run costs FlexCredits and requires the tidy3d pip package plus an API key.
    Tidy3D,
}
