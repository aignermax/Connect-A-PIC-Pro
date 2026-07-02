namespace CAP.Avalonia.ViewModels.Export;

/// <summary>Outcome of the pre-flight check before generating a GDS file.</summary>
public enum GdsPreflightDecision
{
    /// <summary>Environment is ready (or GDS generation is disabled/headless) — continue.</summary>
    Proceed,

    /// <summary>User chose to skip the GDS step; the Nazca script export stands alone.</summary>
    SkipGds,

    /// <summary>User asked to install Nazca now (open settings + start default install).</summary>
    InstallRequested,

    /// <summary>User asked to open the settings without installing.</summary>
    OpenSettingsRequested,
}
