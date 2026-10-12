namespace Anno1800IslandTool.Formats.Discovery;

public static class FilePathNormalizer
{
    public static string Normalize(string path)
        => path.Replace('\\', '/').TrimStart('/');
}
