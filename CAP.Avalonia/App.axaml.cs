using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CAP.Avalonia.DI;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.Views;
using Microsoft.Extensions.DependencyInjection;

namespace CAP.Avalonia;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Registers every feature's services into <paramref name="services"/>.
    /// Extracted from <see cref="OnFrameworkInitializationCompleted"/> so the full DI
    /// graph can be built and validated in a headless test (catches a missing/misplaced
    /// registration that would otherwise only surface as a crash on app start).
    /// </summary>
    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddUpdateFeature();
        services.AddAiAssistantFeature();
        services.AddExportFeature();
        services.AddPythonEnvFeature();
        services.AddCoreServices();
        services.AddCanvasAndPanels();
        services.AddSettingsFeature();
        services.AddPdkOffsetFeature();
        services.AddFdtdFeature();
        services.AddModeSolverFeature();
        services.AddNotificationFeature();
        services.AddHomeFeature();

        services.AddSingleton<MainViewModel>();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainVm = Services.GetRequiredService<MainViewModel>();
            // A .lun file passed on the command line (or via OS file association)
            // is opened by MainWindow's Loaded handler.
            mainVm.StartupDesignFile = CAP.Avalonia.Services.DesignFileArguments
                .FindDesignFile(desktop.Args ?? Array.Empty<string>());
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainVm
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            var mainVm = Services.GetRequiredService<MainViewModel>();
            singleView.MainView = new MainView
            {
                DataContext = mainVm
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
