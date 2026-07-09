using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Solvers;
using CAP.Avalonia.ViewModels.Export.PythonEnvironmentManager;
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
            var script = PythonResolution.FindScript("mode_solve.py");
            // Resolve the interpreter lazily on every solve so a newly activated or
            // freshly auto-installed managed environment is picked up without an app
            // restart (#691 review).
            return new PythonModeSolverService(
                () => prefs.GetCustomPythonPath() ?? PythonResolution.ResolvePythonExecutable(),
                script);
        });
        // Shared: the manual dialog writes the last-entered cross-section here; the
        // probe falls back to it when the PDK carries no geometry.
        services.AddSingleton<CrossSectionDefaultsStore>();
        services.AddTransient(sp =>
        {
            var vm = new ModeSolverViewModel(
                sp.GetRequiredService<IModeSolverService>(),
                sp.GetRequiredService<CrossSectionDefaultsStore>());
            vm.EnsureBackendAsync = MakeEnsureBackend(sp);
            return vm;
        });
        services.AddSingleton(sp =>
        {
            var vm = new ModeProbeViewModel(
                sp.GetRequiredService<IModeSolverService>(),
                sp.GetRequiredService<CrossSectionDefaultsStore>());
            vm.EnsureBackendAsync = MakeEnsureBackend(sp);
            return vm;
        });
        return services;
    }

    /// <summary>
    /// Builds the missing-backend auto-install hook, delegating to the Python
    /// environment manager. Resolved lazily so the mode-solver slice never imports
    /// the env-manager slice at build time (mirrors the gdsfactory-export wiring).
    /// </summary>
    private static Func<string, IProgress<string>, CancellationToken, Task<bool>> MakeEnsureBackend(
        IServiceProvider sp) =>
        (packageSpec, progress, ct) =>
            sp.GetRequiredService<PythonEnvironmentManagerViewModel>()
                .EnsureBackendInstalledAsync(packageSpec, progress, ct);
}
