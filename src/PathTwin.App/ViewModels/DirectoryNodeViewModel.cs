using System.Collections.ObjectModel;
using PathTwin.App.Models;
using PathTwin.App.Services;

namespace PathTwin.App.ViewModels;

public sealed class DirectoryNodeViewModel : ViewModelBase
{
    private readonly Action _selectionChanged;
    private readonly DirectoryTreeService? _treeService;
    private readonly string? _remoteRoot;
    private bool? _isChecked = false;
    private bool _isExpanded;
    private bool _childrenLoaded;
    private bool _loadingChildren;

    public DirectoryNodeViewModel(
        DirectoryNode node,
        DirectoryNodeViewModel? parent,
        Action selectionChanged,
        DirectoryTreeService? treeService = null,
        string? remoteRoot = null)
    {
        Name = node.Name;
        RelativePath = node.RelativePath;
        Parent = parent;
        _selectionChanged = selectionChanged;
        _treeService = treeService;
        _remoteRoot = remoteRoot;
        _childrenLoaded = !node.HasChildren;

        if (node.HasChildren && node.Children.Count == 0)
        {
            // Placeholder — will be replaced when expanded
            Children = [new DirectoryNodeViewModel(
                new DirectoryNode { Name = "…", RelativePath = "", HasChildren = false },
                this, selectionChanged)];
        }
        else
        {
            Children = new ObservableCollection<DirectoryNodeViewModel>(
                node.Children.Select(child => new DirectoryNodeViewModel(child, this, selectionChanged, treeService, remoteRoot)));
        }
    }

    public string Name { get; }
    public string RelativePath { get; }
    public DirectoryNodeViewModel? Parent { get; }
    public ObservableCollection<DirectoryNodeViewModel> Children { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value && !_childrenLoaded)
            {
                _ = LoadChildrenAsync();
            }
        }
    }

    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            var effectiveValue = value;
            if (effectiveValue == null)
                effectiveValue = false;

            SetChecked(effectiveValue, updateChildren: true, updateParent: true);
        }
    }

    public void CollectSelectedTopLevel(ICollection<string> selectedPaths, bool parentSelected = false)
    {
        if (IsChecked == true && !parentSelected)
        {
            selectedPaths.Add(RelativePath);
            parentSelected = true;
        }

        if (parentSelected)
            return;

        foreach (var child in Children)
        {
            if (child._loadingChildren || child.Name == "…")
                continue;

            child.CollectSelectedTopLevel(selectedPaths, parentSelected);
        }
    }

    private async Task LoadChildrenAsync()
    {
        if (_childrenLoaded || _treeService is null || _remoteRoot is null)
            return;

        _loadingChildren = true;
        try
        {
            var nodes = await _treeService.LoadChildrenAsync(_remoteRoot, RelativePath);
            _childrenLoaded = true;

            Children.Clear();
            foreach (var child in nodes)
            {
                Children.Add(new DirectoryNodeViewModel(child, this, _selectionChanged, _treeService, _remoteRoot));
            }

            RefreshFromChildren();
            _selectionChanged();
        }
        catch
        {
            // Keep placeholder on error
        }
        finally
        {
            _loadingChildren = false;
        }
    }

    private void SetChecked(bool? value, bool updateChildren, bool updateParent)
    {
        if (_isChecked == value)
            return;

        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));

        if (updateChildren && value.HasValue)
        {
            foreach (var child in Children)
            {
                if (child._loadingChildren)
                    continue;
                child.SetChecked(value, updateChildren: true, updateParent: false);
            }
        }

        if (updateParent)
            Parent?.RefreshFromChildren();

        _selectionChanged();
    }

    private void RefreshFromChildren()
    {
        if (Children.Count == 0)
            return;

        if (Children.Count == 1 && Children[0]._loadingChildren)
            return;

        var allChecked = Children.All(child => child.IsChecked == true);
        var allUnchecked = Children.All(child => child.IsChecked != true);

        bool? aggregate;
        if (allChecked)
            aggregate = true;
        else if (allUnchecked)
            aggregate = false;
        else
            aggregate = null;

        SetChecked(aggregate, updateChildren: false, updateParent: true);
    }
}
