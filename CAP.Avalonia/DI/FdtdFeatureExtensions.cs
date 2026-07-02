using System;
using System.IO;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Solvers;
using CAP_Core.Export;
using CAP_Core.Solvers.Fdtd;
using Microsoft.Extensions.DependencyInjection;

namespace CAP.Avalonia.DI;

/// <summary>
/// Registers the FDTD S-matrix solver feature: the open-source Meep solver run in a
/// self-provisioning Docker image. Resolves the bridge Dockerfile from the bundled
/// <c>scripts/fdtd/</c> folder.
/// </summary>
internal static class FdtdFeatureExtensions
{
    /// <summary>Adds <see cref="IFdtdSMatrixService"/> backed by the Docker/Meep solver.</summary>
    public static IServiceCollection AddFdtdFeature(this IServiceCollection services)
    {
        services.AddSingleton<IFdtdSMatrixService>(sp =>
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
        return services;
    }
}
