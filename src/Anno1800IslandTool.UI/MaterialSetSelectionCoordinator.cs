using Anno1800IslandTool.Core.Models;

namespace Anno1800IslandTool.UI;

/// <summary>
/// Keeps the material-set selectors of the material and GPU editors aligned when
/// both windows are open on the same project. The project remains the source of
/// truth; this notification only asks the other views to refresh themselves.
/// </summary>
internal static class MaterialSetSelectionCoordinator
{
    public static event EventHandler<MaterialSetSelectionChangedEventArgs>? SelectionChanged;

    public static void Publish(IslandProject project, object source)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);
        SelectionChanged?.Invoke(source, new MaterialSetSelectionChangedEventArgs(project, source));
    }
}

internal sealed class MaterialSetSelectionChangedEventArgs : EventArgs
{
    public MaterialSetSelectionChangedEventArgs(IslandProject project, object source)
    {
        Project = project;
        Source = source;
    }

    public IslandProject Project { get; }
    public object Source { get; }
}
