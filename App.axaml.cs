using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using StreamBox.Services;
using StreamBox.ViewModels;
using StreamBox.Views;

namespace StreamBox;

public partial class App : Application
{
    public IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Log.Info("OnFrameworkInitializationCompleted entered");

        try
        {
            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);
            Services = serviceCollection.BuildServiceProvider();
            Log.Info("DI container built");

            var viewModel = Services.GetRequiredService<MainViewModel>();
            Log.Info("MainViewModel created");

            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.DataContext = viewModel;
            Log.Info("MainWindow created");

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = mainWindow;
            }

            base.OnFrameworkInitializationCompleted();
        }
        catch (Exception ex)
        {
            Log.Error("Fatal startup error in OnFrameworkInitializationCompleted()", ex);
            NativeDialog.ShowError(
                "StreamBox Startup Error",
                "StreamBox failed to finish startup.\n\n" +
                $"Details were written to:\n{Log.LogFilePath}");
            throw;
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestHeaders =
            {
                { "User-Agent", "StreamBox/1.0 (Windows)" }
            }
        });

        services.AddSingleton<DatabaseService>();
        services.AddSingleton<PlaylistService>();
        services.AddSingleton<PlayerService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }
}
