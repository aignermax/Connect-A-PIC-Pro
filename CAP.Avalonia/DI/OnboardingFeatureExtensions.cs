using CAP.Avalonia.ViewModels.Onboarding.FirstStepsTutorial;
using Microsoft.Extensions.DependencyInjection;

namespace CAP.Avalonia.DI;

/// <summary>
/// Registers the onboarding feature: the guided first-steps tour started from
/// the Home screen's "Learn Lunima" card (issue #1080, slice 1 of #769).
/// </summary>
internal static class OnboardingFeatureExtensions
{
    /// <summary>Adds the first-steps tutorial step engine as a singleton over the shared canvas.</summary>
    public static IServiceCollection AddOnboardingFeature(this IServiceCollection services)
    {
        services.AddSingleton<TutorialViewModel>();

        return services;
    }
}
