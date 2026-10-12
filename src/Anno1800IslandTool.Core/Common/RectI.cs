namespace Anno1800IslandTool.Core.Common;

public readonly record struct RectI(int Left, int Top, int Right, int Bottom)
{
    public override string ToString() => $"[{Left}, {Top}, {Right}, {Bottom}]";
}
