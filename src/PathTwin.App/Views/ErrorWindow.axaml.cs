using Avalonia.Controls;
using Avalonia.Interactivity;
using PathTwin.App.Models;
using PathTwin.App.Platform;

namespace PathTwin.App.Views;

public sealed partial class ErrorWindow : Window
{
    private readonly ErrorReport _report;

    public ErrorWindow()
        : this(new ErrorReport())
    {
    }

    public ErrorWindow(ErrorReport report)
    {
        _report = report;
        InitializeComponent();
        DataContext = report;
    }

    private void OpenLogs_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_report.LogFolder))
        {
            new ShellService().OpenFolder(_report.LogFolder);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
