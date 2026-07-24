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
/// Registers the FDTD S-matrix solver feature: the open-source Meep solver run in a
/// self-provisioning Docker image, the Tidy3D cloud solver, and the backend registry
/// holding the user's persisted choice between them.
/// </summary>
internal static class FdtdFeatureExtensions
{
    /// <summary>Adds <see cref="IFdtdSMatrixService"/> backed by the Docker/Meep solver.</summary>
    public static IServiceCollection AddFdtdFeature(this IServiceCollection services)
    {
        services.AddSingleton<DockerFdtdSMatrixService>(sp =>
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
        // The NewComponent editor path binds IFdtdSMatrixService directly and keeps
        // the local/free backend; selectable flows go through FdtdBackendRegistry.
        services.AddSingleton<IFdtdSMatrixService>(sp => sp.GetRequiredService<DockerFdtdSMatrixService>());
        services.AddSingleton<Tidy3dSMatrixService>(sp =>
        {
            var prefs = sp.GetRequiredService<UserPreferencesService>();
            // Resolve the interpreter lazily on every run so a newly activated or
            // freshly auto-installed managed environment is picked up without an app
            // restart (same pattern as the mode solver); the bridge reports
            // missing_backend cleanly when tidy3d is absent.
            return new Tidy3dSMatrixService(
                () => prefs.GetCustomPythonPath() ?? PythonResolution.ResolvePythonExecutable(),
                PythonResolution.FindScript("tidy3d_sparams.py"),
                () => prefs.GetTidy3dApiKey(),
                sp.GetRequiredService<ProcessLaunchFactory>());
        });
        services.AddSingleton(sp => new FdtdBackendRegistry(
            new Dictionary<FdtdBackendType, IFdtdSMatrixService>
            {
                [FdtdBackendType.MeepDocker] = sp.GetRequiredService<DockerFdtdSMatrixService>(),
                [FdtdBackendType.Tidy3D] = sp.GetRequiredService<Tidy3dSMatrixService>(),
            },
            sp.GetRequiredService<UserPreferencesService>()));
        // Guided "Set up FDTD" dialog (issue #649): shown when Docker is missing
        // or its engine is stopped, with platform-specific install/start guidance.
        services.AddSingleton<IDockerSetupDialogService, DockerSetupDialogService>();
        return services;
    }
}
