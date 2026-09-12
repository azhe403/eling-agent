using System;
using Avalonia;
using Eling.Core;
using Eling.Core.Logging;
using Eling.Core.Scope;
using Eling.Desktop.Services;
using Eling.Desktop.ViewModels;
using Eling.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI.Avalonia;
using Serilog;
using Serilog.Debugging;

namespace Eling.Desktop;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        SelfLog.Enable(Console.Error);

        var services = new ServiceCollection();
        ConfigureServices(services);
        var provider = services.BuildServiceProvider();

        BuildAvaloniaApp(provider)
            .StartWithClassicDesktopLifetime(args);

        provider.GetRequiredService<MainViewModel>().Dispose();
        Log.CloseAndFlush();
        provider.Dispose();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        var logsDir = CentralLogDirectory.Resolve();
        var sink = new RollingDailyFileSink(logsDir, "desktop.log", "desktop");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSerilog(dispose: true);
        });

        services.AddSingleton(loggerFactory);
        services.AddSingleton(typeof(ILoggerFactory), loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        services.AddSingleton<BackendSupervisor>();
        services.AddSingleton<ElingApiClient>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<MainWindow>();
    }

    public static AppBuilder BuildAvaloniaApp()
        => BuildAvaloniaApp(null);

    public static AppBuilder BuildAvaloniaApp(IServiceProvider? provider)
        => AppBuilder.Configure(() => new App(provider))
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI(_ => { });
}
