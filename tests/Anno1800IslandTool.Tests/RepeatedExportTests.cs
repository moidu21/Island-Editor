using System.IO.Compression;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Pipeline.Export;
using Anno1800IslandTool.Pipeline.Import;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;

namespace Anno1800IslandTool.Tests;
public class RepeatedExportTests
{
    private static void Add(ZipArchive zip, string path, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open());
        writer.Write(text);
    }

    [Fact] public void ReopenAndExportRefreshesReportsWithoutDuplicates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AnnoExportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "old.zip");
            using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
            {
                Add(zip, "island.xml", "<Root/>");
                Add(zip, "island/rd3d.xml", "<Root/>");
                Add(zip, "_anno1800islandtool/validation-report.txt", "old");
                Add(zip, "_anno1800islandtool/validation-report.txt", "old duplicate");
                Add(zip, "_anno1800islandtool/export-manifest.txt", "old manifest");
                Add(zip, "_anno1800islandtool/native-a7m/extra.data", "native payload");
                Add(zip, "_anno1800islandtool/custom.txt", "keep me");
            }
            var project = new IslandProject();
            project.Metadata.SourceArchivePath = source;
            for (var pass = 0; pass < 3; pass++)
            {
                var output = Path.Combine(dir, $"export{pass}.zip");
                new IslandExporter().Export(project, output, new() { FailOnValidationErrors = false });
                using (var zip = ZipFile.OpenRead(output))
                {
                    Assert.Equal(zip.Entries.Count, zip.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
                    using var report = new StreamReader(zip.GetEntry("_anno1800islandtool/validation-report.txt")!.Open());
                    Assert.DoesNotContain("old duplicate", report.ReadToEnd());
                    Assert.NotNull(zip.GetEntry("_anno1800islandtool/native-a7m/extra.data"));
                    Assert.NotNull(zip.GetEntry("_anno1800islandtool/custom.txt"));
                }
                ZipFile.ExtractToDirectory(output, Path.Combine(dir, $"extracted{pass}"));
                project = new IslandImporter().Import(output);
            }
            NativeIslandConverter.ExtractInterpretedArchive(source, Path.Combine(dir, "legacy"));
            Assert.Equal("native payload", File.ReadAllText(Path.Combine(dir, "legacy/_anno1800islandtool/native-a7m/extra.data")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("island/rd3d.xml", true)]
    [InlineData("../escape.xml", false)]
    public void ExtractionStillRejectsDuplicateDataAndEscapingPaths(string entry, bool duplicate)
    {
        var dir = Path.Combine(Path.GetTempPath(), "AnnoExportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "bad.zip");
            using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
            {
                Add(zip, entry, "first");
                if (duplicate) Add(zip, entry, "second");
            }
            if (duplicate) Assert.Throws<IOException>(() => NativeIslandConverter.ExtractInterpretedArchive(source, Path.Combine(dir, "input")));
            else Assert.Throws<InvalidDataException>(() => NativeIslandConverter.ExtractInterpretedArchive(source, Path.Combine(dir, "input")));
            Assert.False(File.Exists(Path.Combine(dir, "escape.xml")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
