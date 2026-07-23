using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PathTwin.App.Models;
using PathTwin.App.ViewModels;

namespace PathTwin.App.Views;

public sealed partial class MainWindow : Window
{
    private bool _closeApproved;
    private bool _closeConfirmationOpen;

    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.ShowErrorAsync = ShowErrorAsync;
            }
        };
    }

    public void CloseWithoutConfirmation()
    {
        _closeApproved = true;
        Close();
    }

    private async void BrowseRemoteRoot_Click(object? sender, RoutedEventArgs e)
    {
        var window = new LightFolderPickerWindow("Remote Root", ViewModel?.RemoteRoot ?? string.Empty);
        var path = await window.ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(path))
        {
            ViewModel?.SetRemoteRootFromPicker(path);
        }
    }

    private async void BrowseLocalRoot_Click(object? sender, RoutedEventArgs e)
        => await PickFolderAsync("Local Root", path => ViewModel?.SetLocalRootFromPicker(path));

    private async void BrowseHistoryRoot_Click(object? sender, RoutedEventArgs e)
        => await PickFolderAsync("History Root", path => ViewModel?.SetHistoryRootFromPicker(path));

    private async void BrowseLogRoot_Click(object? sender, RoutedEventArgs e)
        => await PickFolderAsync("Log Root", path => ViewModel?.SetLogRootFromPicker(path));

    private async void BrowseRclonePath_Click(object? sender, RoutedEventArgs e)
    {
        var storage = StorageProvider;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "rclone.exe",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Executable")
                {
                    Patterns = ["*.exe"]
                }
            ]
        });

        if (files.Count > 0)
        {
            ViewModel!.RclonePath = files[0].Path.LocalPath;
        }
    }

    private async Task PickFolderAsync(string title, Action<string> assign)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            assign(folders[0].Path.LocalPath);
        }
    }

    private async Task ShowErrorAsync(ErrorReport report)
    {
        var window = new ErrorWindow(report);
        await window.ShowDialog(this);
    }

    private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved)
        {
            return;
        }

        e.Cancel = true;
        if (_closeConfirmationOpen)
        {
            return;
        }

        _closeConfirmationOpen = true;
        try
        {
            var confirmation = ViewModel?.GetExitConfirmationInfo() ?? new ExitConfirmationInfo
            {
                State = "PathTwin is closing",
                CurrentOperation = "No active operation",
                Warning = "Force Exit closes the application immediately."
            };
            var window = new ExitConfirmationWindow(confirmation);
            var forceExit = await window.ShowDialog<bool?>(this);
            if (forceExit == true)
            {
                _closeApproved = true;
                Close();
            }
        }
        finally
        {
            _closeConfirmationOpen = false;
        }
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;
}
