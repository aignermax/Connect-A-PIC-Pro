using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Solvers;
using CAP.Avalonia.ViewModels.Solvers;
using CAP.Avalonia.ViewModels.Solvers.ModeProbe;
using CAP_Core.Solvers.ModeProbe;
using CAP_Core.Solvers.ModeSolver;
using Microsoft.Extensions.DependencyInjection;

namespace CAP.Avalonia.DI;

/// <summary>
/// Registers the mode-solver feature: a Python-subprocess service computing n_eff/n_g/
/// mode field, its manual dialog ViewModel, and the canvas mode-probe flyout (issue #691).
/// </summary>
internal static class ModeSolverFeatureExtensions
{
    /// <summary>
    /// Adds <see cref="IModeSolverService"/>, <see cref="ModeSolverViewModel"/>,
    /// the shared <see cref="CrossSectionDefaultsStore"/>, and <see cref="ModeProbeViewModel"/>.
    /// </summary>
    public static IServiceCollection AddModeSolverFeature(this IServiceCollection services)
    {
        services.AddSingleton<IModeSolverService>(sp =>
        {
            var prefs = sp.GetRequiredService<UserPreferencesService>();
            var python = prefs.GetCustomPythonPath() ?? PythonResolution.ResolvePythonExecutable();
            var script = PythonResolution.FindScript("mode_solve.py");
            return new PythonModeSolverService(python, script);
        });
        // Shared: the manual dialog writes the last-entered cross-section here; the
        // probe falls back to it when the PDK carries no geometry.
        services.AddSingleton<CrossSectionDefaultsStore>();
        services.AddTransient(sp => new ModeSolverViewModel(
            sp.GetRequiredService<IModeSolverService>(),
            sp.GetRequiredService<CrossSectionDefaultsStore>()));
        services.AddSingleton(sp => new ModeProbeViewModel(
            sp.GetRequiredService<IModeSolverService>(),
            sp.GetRequiredService<CrossSectionDefaultsStore>()));
        return services;
    }
}
