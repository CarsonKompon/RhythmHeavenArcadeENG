namespace ModdingTool.App.ViewModels;

public sealed record OutputGroupViewModel(
    Guid? Id,
    string Name,
    string Background,
    bool IsExpanded,
    int Order);