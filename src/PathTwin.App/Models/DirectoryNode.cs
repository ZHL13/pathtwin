namespace PathTwin.App.Models;

public sealed class DirectoryNode
{
    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public List<DirectoryNode> Children { get; set; } = [];
    public bool HasChildren { get; set; }
    public bool IsSelectable { get; set; } = true;
    public bool IsLimitNotice { get; set; }
}
