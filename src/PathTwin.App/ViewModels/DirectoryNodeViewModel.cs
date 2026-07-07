using System.Collections.ObjectModel;
using Avalonia.Threading;
using PathTwin.App.Models;
using PathTwin.App.Services;

namespace PathTwin.App.ViewModels;

public sealed class DirectoryNodeViewModel : ViewModelBase
{
    private readonly Action _selectionChanged;
    private readonly DirectoryTreeService? _treeService;
    private readonly string? _remoteRoot;
    private readonly IReadOnlySet<string>? _restoredSelectedPaths;
    private bool _restoredSelectedPathsActive;
    private bool? _isChecked = false;
    private bool _isExpanded;
    private bool _childrenLoaded;
    private bool _loadingChildren;
    private bool _suppressSelectionChanged;

    public DirectoryNodeViewModel(
        DirectoryNode node,
        DirectoryNodeViewModel? parent,
        Action selectionChanged,
        DirectoryTreeService? treeService = null,
        string? remoteRoot = null,
        IReadOnlySet<string>? restoredSelectedPaths = null)
    {
        Name = node.Name;
        RelativePath = node.RelativePath;
        IsSelectable = node.IsSelectable;
        IsLimitNotice = node.IsLimitNotice;
        Parent = parent;
        _selectionChanged = selectionChanged;
        _treeService = treeService;
        _remoteRoot = remoteRoot;
        _restoredSelectedPaths = restoredSelectedPaths;
        _restoredSelectedPathsActive = IsSelectable && restoredSelectedPaths is not null;
        _childrenLoaded = !node.HasChildren || node.IsLimitNotice;

        if (node.HasChildren && node.Children.Count == 0)
        {
            // Placeholder — will be replaced when expanded
            Children = [new DirectoryNodeViewModel(
                new DirectoryNode { Name = "Loading...", RelativePath = "", HasChildren = false, IsSelectable = false },
                this, selectionChanged)];
        }
        else
        {
            Children = new ObservableCollection<DirectoryNodeViewModel>(
                node.Children.Select(child => new DirectoryNodeViewModel(
                    child,
                    this,
                    selectionChanged,
                    treeService,
                    remoteRoot,
                    restoredSelectedPaths)));
        }

        ApplyRestoredSelectionState();
    }

    public string Name { get; }
    public string RelativePath { get; }
    public bool IsSelectable { get; }
    public bool IsLimitNotice { get; }
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
            if (!IsSelectable)
                return;

            var effectiveValue = value;
            if (effectiveValue == null)
                effectiveValue = false;

            var clearedRestoredSelections = ClearRestoredSelectionsForSubtree();
            SetChecked(effectiveValue, updateChildren: true, updateParent: true);
            if (clearedRestoredSelections && !_suppressSelectionChanged)
                _selectionChanged();
        }
    }

    public void CollectSelectedTopLevel(ICollection<string> selectedPaths, bool parentSelected = false)
    {
        if (!IsSelectable)
            return;

        if (IsChecked == true && !parentSelected)
        {
            selectedPaths.Add(RelativePath);
            parentSelected = true;
        }

        if (parentSelected)
            return;

        if (IsChecked is null && !_childrenLoaded && _restoredSelectedPathsActive && _restoredSelectedPaths is not null)
        {
            foreach (var path in _restoredSelectedPaths.Where(IsDescendantPath))
            {
                selectedPaths.Add(path);
            }

            return;
        }

        foreach (var child in Children)
        {
            if (child._loadingChildren || !child.IsSelectable)
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
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _childrenLoaded = true;
                Children.Clear();
                foreach (var child in nodes)
                {
                    var childViewModel = new DirectoryNodeViewModel(
                        child,
                        this,
                        _selectionChanged,
                        _treeService,
                        _remoteRoot,
                        _restoredSelectedPathsActive ? _restoredSelectedPaths : null);
                    Children.Add(childViewModel);
                    if (IsChecked == true && childViewModel.IsSelectable)
                    {
                        childViewModel.SetChecked(true, updateChildren: true, updateParent: false);
                    }
                }

                if (IsChecked != true)
                {
                    RefreshFromChildren();
                }

                _selectionChanged();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _childrenLoaded = true;
                Children.Clear();
                Children.Add(new DirectoryNodeViewModel(
                    new DirectoryNode
                    {
                        Name = $"Could not load this folder: {ex.Message}",
                        RelativePath = string.Empty,
                        IsSelectable = false,
                        IsLimitNotice = true
                    },
                    this,
                    _selectionChanged));
            });
        }
        finally
        {
            _loadingChildren = false;
        }
    }

    private void SetChecked(bool? value, bool updateChildren, bool updateParent)
    {
        if (!IsSelectable)
            return;

        if (_isChecked == value)
            return;

        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));

        if (updateChildren && value.HasValue)
        {
            foreach (var child in Children)
            {
                if (child._loadingChildren || !child.IsSelectable)
                    continue;
                child.SetChecked(value, updateChildren: true, updateParent: false);
            }
        }

        if (updateParent)
            Parent?.RefreshFromChildren();

        if (!_suppressSelectionChanged)
            _selectionChanged();
    }

    private void RefreshFromChildren()
    {
        var selectableChildren = Children.Where(child => child.IsSelectable).ToArray();
        if (selectableChildren.Length == 0)
            return;

        if (selectableChildren.Length == 1 && selectableChildren[0]._loadingChildren)
            return;

        var allChecked = selectableChildren.All(child => child.IsChecked == true);
        var allUnchecked = selectableChildren.All(child => child.IsChecked == false);

        bool? aggregate;
        if (allChecked)
            aggregate = true;
        else if (allUnchecked)
            aggregate = false;
        else
            aggregate = null;

        _suppressSelectionChanged = true;
        try
        {
            SetChecked(aggregate, updateChildren: false, updateParent: true);
        }
        finally
        {
            _suppressSelectionChanged = false;
        }
    }

    private void ApplyRestoredSelectionState()
    {
        if (!_restoredSelectedPathsActive || _restoredSelectedPaths is null)
            return;

        if (_restoredSelectedPaths.Contains(RelativePath))
        {
            _isChecked = true;
            return;
        }

        if (_restoredSelectedPaths.Any(IsDescendantPath))
        {
            _isChecked = null;
        }
    }

    private bool IsDescendantPath(string candidate)
    {
        if (string.IsNullOrEmpty(RelativePath))
            return false;

        return candidate.StartsWith(RelativePath + "/", StringComparison.OrdinalIgnoreCase);
    }

    private bool ClearRestoredSelectionsForSubtree()
    {
        var cleared = _restoredSelectedPathsActive;
        _restoredSelectedPathsActive = false;

        foreach (var child in Children)
        {
            cleared |= child.ClearRestoredSelectionsForSubtree();
        }

        return cleared;
    }
}
