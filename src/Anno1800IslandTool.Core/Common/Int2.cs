namespace Anno1800IslandTool.Core.Common;

public readonly record struct Int2(int X, int Y)
{
    public override string ToString() => $"{X} x {Y}";
}
