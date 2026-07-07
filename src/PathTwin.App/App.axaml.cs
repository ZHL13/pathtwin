using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PathTwin.App.Backends;
using PathTwin.App.Configuration;
using PathTwin.App.Constants;
using PathTwin.App.Logging;
using PathTwin.App.Platform;
using PathTwin.App.Services;
using PathTwin.App.Sync;
using PathTwin.App.ViewModels;
using PathTwin.App.Views;

namespace PathTwin.App;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var configService = new ConfigService();
            var logService = new LogService();
            var shellService = new ShellService();
            var taskSchedulerService = new TaskSchedulerService();
            var fileScanner = new FileScanner();
            var planner = new SyncPlanner();
            var backendFactory = new SyncBackendFactory(logService);
            var executor = new SyncExecutor(logService);
            var sessionService = new WorkSessionService(
                configService,
                logService,
                fileScanner,
                planner,
                backendFactory,
                executor);

            var viewModel = new MainWindowViewModel(
                AppConstants.ApplicationName,
                configService,
                logService,
                shellService,
                taskSchedulerService,
                new DirectoryTreeService(),
                sessionService);

            var mainWindow = new MainWindow
            {
                DataContext = viewModel
            };

            desktop.MainWindow = mainWindow;
            viewModel.RequestExit += () => desktop.Shutdown();
            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
