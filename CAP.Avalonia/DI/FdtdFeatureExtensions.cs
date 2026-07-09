using System;
using System.Collections.Generic;
using System.IO;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Solvers;
using CAP_Core.Export;
using CAP_Core.Solvers.Fdtd;
using Microsoft.Extensions.DependencyInjection;

namespace CAP.Avalonia.DI;

/// <summary>
/// Registers the FDTD S-matrix solver feature: the open-source Meep solver run in
/// a self-provisioning Docker image, the Tidy3D cloud solver, and the backend
/// registry that persists the user's choice between them.
/// </summary>
internal static class FdtdFeatureExtensions
{
    /// <summary>
    /// Adds the FDTD backends and <see cref="FdtdBackendRegistry"/>.
    /// <see cref="IFdtdSMatrixService"/> stays registered (resolving to the
    /// user-selected backend) for consumers that don't need the picker.
    /// </summary>
    public static IServiceCollection AddFdtdFeature(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var dockerfile = PythonResolution.FindScript("fdtd", "Dockerfile");
            // Build context = the scripts/ dir (parent of scripts/fdtd) so the small
            // bridge script is COPYable without shipping the whole repo to the daemon.
            var buildContext = Directory.GetParent(Path.GetDirectoryName(dockerfile)!)?.FullName
                               ?? AppDomain.CurrentDomain.BaseDirectory;
            return new DockerFdtdSMatrixService(
                "lunima-meep:1", dockerfile, buildContext,
                launchFactory: sp.GetRequiredService<ProcessLaunchFactory>());
        });

        services.AddSingleton(sp =>
        {
            var prefs = sp.GetRequiredService<UserPreferencesService>();
            var python = prefs.GetCustomPythonPath() ?? PythonResolution.ResolvePythonExecutable();
            var script = PythonResolution.FindScript("tidy3d_sparams.py");
            return new Tidy3dSMatrixService(
                python, script, prefs.GetTidy3dApiKey,
                launchFactory: sp.GetRequiredService<ProcessLaunchFactory>());
        });

        services.AddSingleton(sp => new FdtdBackendRegistry(
            new Dictionary<FdtdBackendType, IFdtdSMatrixService>
            {
                [FdtdBackendType.MeepDocker] = sp.GetRequiredService<DockerFdtdSMatrixService>(),
                [FdtdBackendType.Tidy3D] = sp.GetRequiredService<Tidy3dSMatrixService>(),
            },
            sp.GetRequiredService<UserPreferencesService>()));

        services.AddSingleton<IFdtdSMatrixService>(sp =>
            sp.GetRequiredService<FdtdBackendRegistry>().CurrentService);

        return services;
    }
}
