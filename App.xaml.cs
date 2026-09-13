using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using GameTrainer.Services;
using GameTrainer.ViewModels;
using GameTrainer.Views;
using iNKORE.UI.WPF.Modern;

namespace GameTrainer;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.txt"), ev.ExceptionObject.ToString()); } catch { }
        };
        DispatcherUnhandledException += (s, ev) =>
        {
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.txt"), ev.Exception.ToString()); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (s, ev) =>
        {
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.txt"), ev.Exception.ToString()); } catch { }
        };

        ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IHttpClientProvider, HttpClientProvider>();
        services.AddSingleton<FlingScraperService>();
        services.AddSingleton<TrainerDownloadService>();
        services.AddSingleton<TrainerDataService>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }
}
