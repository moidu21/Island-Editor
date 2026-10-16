using System.IO.Compression;

namespace Anno1800IslandTool.Formats.Archive;

public sealed class ZipArchiveWriter
{
    public void WriteFromDirectory(string sourceDirectory, string outputZipPath)
    {
        if (File.Exists(outputZipPath))
        {
            File.Delete(outputZipPath);
        }

        ZipFile.CreateFromDirectory(sourceDirectory, outputZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
    }
}
