namespace CAP.Avalonia.ViewModels.Analysis;

/// <summary>How the design is simulated when Run (L) is invoked.</summary>
public enum SimulationMode
{
    /// <summary>Continuous-wave frequency-domain steady state (default).</summary>
    Cw,
    /// <summary>Time-domain transient: pulse response / eye-diagram basis.</summary>
    Transient,
}
