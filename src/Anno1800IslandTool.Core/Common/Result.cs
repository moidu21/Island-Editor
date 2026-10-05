namespace Anno1800IslandTool.Core.Common;

public sealed class Result
{
    public bool IsSuccess { get; private init; }
    public string? Error { get; private init; }

    public static Result Success() => new() { IsSuccess = true };
    public static Result Failure(string error) => new() { IsSuccess = false, Error = error };
}
