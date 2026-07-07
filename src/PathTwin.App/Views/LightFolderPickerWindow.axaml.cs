using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PathTwin.App.Views;

public sealed partial class LightFolderPickerWindow : Window
{
    private const int MaxEntriesPerLevel = 300;
    private readonly ObservableCollection<PathEntry> _items = [];
    private string _currentPath = string.Empty;

    public LightFolderPickerWindow()
    {
        InitializeComponent();
        ItemsList.ItemsSource = _items;
    }

    public LightFolderPickerWindow(string title, string initialPath)
        : this()
    {
        Title = title;
        PathBox.Text = initialPath;
        Opened += async (_, _) => await LoadCurrentPathAsync();
    }

    private async void Load_Click(object? sender, RoutedEventArgs e) => await LoadCurrentPathAsync();

    private async void Up_Click(object? sender, RoutedEventArgs e)
    {
        var path = PathBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var parent = Directory.GetParent(path);
        if (parent is null)
        {
            return;
        }

        PathBox.Text = parent.FullName;
        await LoadCurrentPathAsync();
    }

    private async void PathBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await LoadCurrentPathAsync();
        }
    }

    private async void ItemsList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ItemsList.SelectedItem is not PathEntry { IsDirectory: true } entry)
        {
            return;
        }

        PathBox.Text = entry.FullPath;
        await LoadCurrentPathAsync();
    }

    private void Select_Click(object? sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_currentPath))
        {
            StatusText.Text = "Folder does not exist or is not reachable.";
            return;
        }

        Close(_currentPath);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private async Task LoadCurrentPathAsync()
    {
        var requestedPath = PathBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            StatusText.Text = "Enter a folder path.";
            return;
        }

        StatusText.Text = "Loading...";
        _items.Clear();

        try
        {
            var result = await Task.Run(() => LoadEntries(requestedPath));
            _currentPath = result.FullPath;
            PathBox.Text = result.FullPath;

            foreach (var item in result.Entries)
            {
                _items.Add(item);
            }

            StatusText.Text = result.HasMore
                ? $"Showing first {MaxEntriesPerLevel} entries in this folder."
                : $"{result.DirectoryCount} folder(s), {result.FileCount} file(s).";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
            or IOException
            or DirectoryNotFoundException
            or PathTooLongException
            or ArgumentException
            or NotSupportedException)
        {
            StatusText.Text = ex.Message;
        }
    }

    private static LoadResult LoadEntries(string requestedPath)
    {
        var fullPath = Path.GetFullPath(requestedPath);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Folder does not exist: {requestedPath}");
        }

        var directories = new List<PathEntry>();
        var files = new List<PathEntry>();
        var hasMore = false;

        foreach (var directory in EnumerateDirectoriesSafe(fullPath))
        {
            if (directories.Count + files.Count >= MaxEntriesPerLevel)
            {
                hasMore = true;
                break;
            }

            directories.Add(new PathEntry(Path.GetFileName(directory), directory, true));
        }

        if (!hasMore)
        {
            foreach (var file in EnumerateFilesSafe(fullPath))
            {
                if (directories.Count + files.Count >= MaxEntriesPerLevel)
                {
                    hasMore = true;
                    break;
                }

                files.Add(new PathEntry(Path.GetFileName(file), file, false));
            }
        }

        var entries = directories
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Concat(files.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        return new LoadResult(fullPath, entries, directories.Count, files.Count, hasMore);
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string path)
    {
        return Directory.EnumerateDirectories(path, "*", new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        });
    }

    private static IEnumerable<string> EnumerateFilesSafe(string path)
    {
        return Directory.EnumerateFiles(path, "*", new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        });
    }

    private sealed record LoadResult(
        string FullPath,
        IReadOnlyList<PathEntry> Entries,
        int DirectoryCount,
        int FileCount,
        bool HasMore);

    public sealed record PathEntry(string Name, string FullPath, bool IsDirectory)
    {
        public string Marker => IsDirectory ? "DIR" : "FILE";
    }
}
