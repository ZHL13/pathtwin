using Avalonia.Controls;
using Avalonia.Interactivity;
using PathTwin.App.Models;

namespace PathTwin.App.Views;

public sealed partial class ExitConfirmationWindow : Window
{
    public ExitConfirmationWindow()
        : this(new ExitConfirmationInfo())
    {
    }

    public ExitConfirmationWindow(ExitConfirmationInfo confirmation)
    {
        InitializeComponent();
        DataContext = confirmation;
    }

    private void KeepWorking_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void ForceExit_Click(object? sender, RoutedEventArgs e) => Close(true);
}
