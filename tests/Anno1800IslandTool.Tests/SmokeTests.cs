
using System.IO.Compression;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Enums;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Core.Validation;
using Anno1800IslandTool.Formats.Archive;
using Anno1800IslandTool.Formats.Discovery;
using Anno1800IslandTool.Formats.Readers;
using Anno1800IslandTool.Formats.Writers;
using Anno1800IslandTool.Pipeline.Editing;
using Anno1800IslandTool.Pipeline.Export;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.Pipeline.Validation;
using Xunit;

namespace Anno1800IslandTool.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void ZipArchiveReader_Session_Reuses_Archive_For_Index_Text_And_Batches()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"anno-v465-session-{Guid.NewGuid():N}.zip");
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                var root = archive.CreateEntry("island/root.xml");
                using (var writer = new StreamWriter(root.Open())) writer.Write("<root />");
                var page = archive.CreateEntry("meshes/2/0x0.xml");
                using (var writer = new StreamWriter(page.Open())) writer.Write("<mesh />");
                var tint = archive.CreateEntry("tintmaps/2/0x0.dds");
                using (var stream = tint.Open()) stream.Write(new byte[] { 7, 8, 9 });
            }

            var reader = new ZipArchiveReader();
            using var session = reader.OpenSession(temp);
            Assert.Equal(3, session.ReadIndex().Count);
            Assert.Equal("<root />", session.ReadEntryText("island/root.xml"));

            var batches = new Dictionary<string, string>();
            session.ProcessEntriesTextBatched(new[] { "meshes/2/0x0.xml" }, 1, batch =>
            {
                foreach (var pair in batch) batches[pair.Key] = pair.Value;
            });
            Assert.Equal("<mesh />", batches["meshes/2/0x0.xml"]);

            byte[]? tintBytes = null;
            session.ProcessEntriesBytes(new[] { "tintmaps/2/0x0.dds" }, (_, bytes) => tintBytes = bytes);
            Assert.Equal(new byte[] { 7, 8, 9 }, tintBytes);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    [Fact]
    public void MeshPageReader_Fast_Number_Decoder_Preserves_All_Eight_Signed_Values()
    {
        const string xml = """
<Content><width>1</width><height>1</height><Meshes>
  <None><Indices>0 2 1 -4</Indices><Vertices>
    48 28795 60 24575 -1408 -164 23680 30469
    -10 32767 99 -1 -2 -3 -4 -5
  </Vertices><UsedMaterialBits>-2147483648</UsedMaterialBits><GroundObjects /></None>
</Meshes></Content>
""";

        var page = new MeshPageReader().Read("meshes/2/0x0.xml", new TerrainPageKey(2, 0, 0), xml, retainRawText: false);

        Assert.Single(page.Clusters);
        Assert.Equal(new[] { 0, 2, 1, -4 }, page.Clusters[0].Indices);
        Assert.Equal(2, page.Clusters[0].Vertices.Count);
        Assert.Equal(new[] { 48, 28795, 60, 24575, -1408, -164, 23680, 30469 }, page.Clusters[0].Vertices[0].RawValues);
        Assert.Equal(new[] { -10, 32767, 99, -1, -2, -3, -4, -5 }, page.Clusters[0].Vertices[1].RawValues);
        Assert.Equal(unchecked((int)0x80000000), page.Clusters[0].UsedMaterialBits);
        Assert.False(page.RawUnknownFields.ContainsKey("__raw"));
    }

    [Fact]
    public void NormalMapPageReader_LowMemory_Keeps_Numeric_Samples_Without_Text_Byte_Copy()
    {
        const string xml = """
<Content><HeightMap>
  <Downsampled><width>3</width><height>1</height><map>-10 0 65535</map></Downsampled>
  <Deltas>
    <None><Data><width>2</width><height>1</height><map>1 255</map></Data><ScaleFactor>4</ScaleFactor><QuantBits>8</QuantBits></None>
  </Deltas>
</HeightMap></Content>
""";

        var page = new NormalMapPageReader().Read("normalmaps/2/0x0.xml", new TerrainPageKey(2, 0, 0), xml, retainRawText: false);

        Assert.Equal(new[] { -10, 0, 65535 }, page.DownsampledSamples);
        Assert.Empty(page.DownsampledData);
        Assert.Single(page.DeltaLevels);
        Assert.Equal(new[] { 1, 255 }, page.DeltaLevels[0].QuantizedSamples);
        Assert.Empty(page.DeltaLevels[0].RawData);
        Assert.Equal(4, page.DeltaLevels[0].ScaleFactor);
        Assert.Equal(8, page.DeltaLevels[0].QuantBits);
    }

    [Fact]
    public void ZipArchiveReader_ReadEntriesBytes_Uses_Single_Bulk_Call_Semantics()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"anno-v40-{Guid.NewGuid():N}.zip");
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                var first = archive.CreateEntry("a/one.txt");
                using (var writer = new StreamWriter(first.Open())) writer.Write("one");
                var second = archive.CreateEntry("b/two.txt");
                using (var writer = new StreamWriter(second.Open())) writer.Write("two");
            }

            var reader = new ZipArchiveReader();
            var payloads = reader.ReadEntriesBytes(temp, new[] { "a/one.txt", "b/two.txt" });

            Assert.Equal("one", System.Text.Encoding.UTF8.GetString(payloads["a/one.txt"]));
            Assert.Equal("two", System.Text.Encoding.UTF8.GetString(payloads["b/two.txt"]));
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    [Fact]
    public void ZipArchiveReader_ProcessEntriesTextBatched_Preserves_Text_And_Batch_Limit()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"anno-v462-{Guid.NewGuid():N}.zip");
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var item in new[] { ("a/one.xml", "un"), ("b/two.xml", "deux"), ("c/three.xml", "trois") })
                {
                    var entry = archive.CreateEntry(item.Item1);
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(item.Item2);
                }
            }

            var reader = new ZipArchiveReader();
            var received = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var largestBatch = 0;
            reader.ProcessEntriesTextBatched(temp, new[] { "a/one.xml", "b/two.xml", "c/three.xml" }, 2, batch =>
            {
                largestBatch = Math.Max(largestBatch, batch.Count);
                foreach (var pair in batch)
                {
                    received[pair.Key] = pair.Value;
                }
            });

            Assert.Equal(2, largestBatch);
            Assert.Equal("un", received["a/one.xml"]);
            Assert.Equal("deux", received["b/two.xml"]);
            Assert.Equal("trois", received["c/three.xml"]);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    [Fact]
    public void ZipArchiveReader_ProcessEntriesBytes_Transfers_One_Entry_At_A_Time()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"anno-v462-dds-{Guid.NewGuid():N}.zip");
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                var first = archive.CreateEntry("tint/0.dds");
                using (var stream = first.Open()) stream.Write(new byte[] { 1, 2, 3 });
                var second = archive.CreateEntry("tint/1.dds");
                using (var stream = second.Open()) stream.Write(new byte[] { 4, 5 });
            }

            var reader = new ZipArchiveReader();
            var received = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            reader.ProcessEntriesBytes(temp, new[] { "tint/0.dds", "tint/1.dds" }, (path, bytes) => received[path] = bytes);

            Assert.Equal(new byte[] { 1, 2, 3 }, received["tint/0.dds"]);
            Assert.Equal(new byte[] { 4, 5 }, received["tint/1.dds"]);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    [Fact]
    public void TerrainBrushSpatialIndex_Returns_Only_Vertices_In_Radius()
    {
        var page = new MeshPage { Key = new TerrainPageKey(0, 0, 0), DecodeStatus = "Décodée", HasEditablePayload = true };
        var cluster = new MeshCluster();
        cluster.Vertices.Add(new MeshVertex { Raw0 = 10, Raw1 = MeshVertex.EncodeDecodedHeight(5), Raw2 = 10 });
        cluster.Vertices.Add(new MeshVertex { Raw0 = 100, Raw1 = MeshVertex.EncodeDecodedHeight(7), Raw2 = 100 });
        page.Clusters.Add(cluster);

        var index = TerrainBrushSpatialIndex.Build(new[] { page }, 0, preferredCellSize: 16);
        var hits = index.QueryCircle(10, 10, 12);

        Assert.Single(hits);
        Assert.Equal(5, hits[0].OriginalDecoded);
    }

    [Fact]
    public void DimensionInferenceService_Computes_Known_Values()
    {
        var service = new DimensionInferenceService();

        Assert.Equal(6144, service.ComputeGridWidth(192));
        Assert.Equal(385, service.ComputeHeightMapSize(192));
        Assert.Equal(768, service.ComputeTintMapSize(192));
        Assert.Equal(24, service.ComputeActiveMapGrid(192));
    }

    [Fact]
    public void IslandRootReader_Reads_MapSize_Grid_And_SlotObjects()
    {
        const string sample = """
<Content>
  <MapSize>192 192</MapSize>
  <ActiveMapGrid>
    <x>24</x>
    <y>24</y>
  </ActiveMapGrid>
  <ActiveMapRect>24 16 176 168</ActiveMapRect>
  <ObjectMetaInfo>
    <SlotObjects>
      <None>
        <value>
          <id>1</id>
        </value>
      </None>
      <None>
        <None>
          <ObjectId>-9218868437227405268</ObjectId>
          <ObjectGuid>1000253</ObjectGuid>
          <Position>98,711365 7,274233 120,144775</Position>
        </None>
      </None>
    </SlotObjects>
  </ObjectMetaInfo>
</Content>
""";

        var root = new IslandRootReader().Read(sample);

        Assert.Equal(192, root.MapSize);
        Assert.Equal(24, root.ActiveMapGrid.X);
        Assert.Equal(24, root.ActiveMapGrid.Y);
        Assert.Single(root.SlotObjects);
        Assert.Equal(1000253.ToString(), root.SlotObjects[0].ObjectGuid);
    }




    [Fact]
    public void ActiveMapGridMaskCodec_Roundtrips_Rect_To_Bits()
    {
        var grid = new Int2(40, 40);
        var rect = new RectI(24, 40, 296, 312);

        var bits = ActiveMapGridMaskCodec.EncodeRect(grid, rect);
        var mask = ActiveMapGridMaskCodec.Decode(grid, bits, default);
        var decodedRect = ActiveMapGridMaskCodec.ComputeActiveRect(grid, mask, default);

        Assert.Equal(80, bits.Count);
        Assert.Equal(rect.Left, decodedRect.Left);
        Assert.Equal(rect.Top, decodedRect.Top);
        Assert.Equal(rect.Right, decodedRect.Right);
        Assert.Equal(rect.Bottom, decodedRect.Bottom);
    }


    [Fact]
    public void ActiveMapGridMaskCodec_Expands_Only_Outer_Edge_Without_Filling_Rectangle()
    {
        var grid = new Int2(5, 5);
        var mask = new bool[25];
        mask[(1 * 5) + 2] = true;
        mask[(2 * 5) + 2] = true;
        mask[(3 * 5) + 2] = true;

        var expanded = ActiveMapGridMaskCodec.ExpandDirectionalOuterEdges(mask, grid, leftCells: 1, topCells: 0, rightCells: 0, bottomCells: 0);

        Assert.True(expanded[(1 * 5) + 1]);
        Assert.True(expanded[(2 * 5) + 1]);
        Assert.True(expanded[(3 * 5) + 1]);
        Assert.False(expanded[(0 * 5) + 1]);
        Assert.False(expanded[(4 * 5) + 1]);
        Assert.Equal(6, ActiveMapGridMaskCodec.CountActiveCells(expanded));
    }

    [Fact]
    public void ProjectTextRegenerator_WaterMask_Does_Not_Keep_An_Underwater_Collision_Halo()
    {
        var rawHeights = new[] { 0.20f, -0.10f, -1.0f, -4.0f }
            .Select(TerrainHeightEncoding.WorldHeightToGameDataRaw)
            .Select(static value => (float)value)
            .ToArray();
        var project = new IslandProject
        {
            Metadata = new IslandMetadata { MapSize = 4 },
            Root = new IslandRootDefinition
            {
                MapSize = 4,
                ActiveMapGrid = new Int2(1, 1),
                ActiveMapRect = new RectI(0, 0, 8, 8),
                ActiveMapGridBits = new List<uint> { 1 }
            },
            GameData = new GameDataDefinition
            {
                TerrainHeightMap = new CoarseHeightMap
                {
                    Width = 4,
                    Height = 2,
                    SampleEncoding = TerrainHeightSampleEncoding.GameDataRawUInt16,
                    Samples = rawHeights.Concat(rawHeights).ToArray()
                }
            },
            RawContent = new RawProjectContent
            {
                GameDataText = "<GameData><TerrainManager><HeightMap><Width>4</Width><Height>2</Height><HeightMap>0 0 0 0 0 0 0 0</HeightMap></HeightMap></TerrainManager><WorldManager><Water><x>4</x><y>1</y><bits>15</bits></Water></WorldManager></GameData>"
            }
        };

        new ProjectTextRegenerator().RegenerateGameDataText(project);

        var water = Regex.Match(project.RawContent.GameDataText!, @"<Water>(?<body>.*?)</Water>", RegexOptions.Singleline).Groups["body"].Value;
        Assert.Contains("<bits>1 0 0 0</bits>", water);
    }

    [Fact]
    public void Exporter_Omits_Terrain_Pages_Suppressed_By_The_Drawn_Footprint()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Anno1800IslandToolTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var sourceZip = Path.Combine(tempDir, "source.zip");
        using (var archive = ZipFile.Open(sourceZip, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("island/meshes/2/0x0.xml").Open())) writer.Write("<mesh />");
            using (var writer = new StreamWriter(archive.CreateEntry("island/keep.txt").Open())) writer.Write("keep");
        }

        var project = new IslandProject
        {
            Metadata = new IslandMetadata { SourceArchivePath = sourceZip, ProjectName = "island" }
        };
        project.SuppressedTerrainPagePaths.Add("island/meshes/2/0x0.xml");
        var outputZip = Path.Combine(tempDir, "export.zip");

        new IslandExporter().Export(project, outputZip, new ExportOptions
        {
            FailOnValidationErrors = false,
            IncludeValidationReport = false,
            IncludeProjectSummary = false,
            IncludeExportManifest = false,
            AddGeneratedTerrainEntriesWhenMissing = false,
            IncludeTerrainDirtyAreaReport = false
        });

        using var exported = ZipFile.OpenRead(outputZip);
        Assert.Null(exported.GetEntry("island/meshes/2/0x0.xml"));
        Assert.NotNull(exported.GetEntry("island/keep.txt"));
    }

    [Fact]
    public void IslandRootReader_Reads_And_Preserves_ActiveMapGridBits()
    {
        const string sample = """
<Content>
  <MapSize>64 64</MapSize>
  <ActiveMapGrid>
    <x>8</x>
    <y>8</y>
    <bits>0 24 60 126 126 60 24 0</bits>
  </ActiveMapGrid>
  <ActiveMapRect>8 8 56 56</ActiveMapRect>
</Content>
""";

        var root = new IslandRootReader().Read(sample);
        var text = new IslandRootWriter().Write(root);

        Assert.Equal(8, root.ActiveMapGrid.X);
        Assert.Equal(8, root.ActiveMapGrid.Y);
        Assert.Equal(8, root.ActiveMapGridBits.Count);
        Assert.Contains("<bits>0 24 60 126 126 60 24 0</bits>", text);
    }

    [Fact]
    public void GameDataReader_Reads_Coarse_Samples_PlayableArea_And_Spawns()
    {
        const string sample = """
<GameData>
  <TerrainManager>
    <HeightMap>
      <Width>3</Width>
      <Height>2</Height>
      <HeightMap>10 20 30 40 50 60</HeightMap>
    </HeightMap>
  </TerrainManager>
  <PlayableArea>0 1 2 3</PlayableArea>
  <AreaIDs>
    <None>A1</None>
  </AreaIDs>
  <SpawnAreaPoints>
    <None><Position>1,5 2,5 3,5</Position></None>
  </SpawnAreaPoints>
</GameData>
""";

        var gameData = new GameDataReader().Read(sample);

        Assert.Equal(3, gameData.TerrainHeightMap.Width);
        Assert.Equal(6, gameData.TerrainHeightMap.Samples.Length);
        Assert.Equal(1, gameData.PlayableArea.Top);
        Assert.Single(gameData.Areas);
        Assert.Single(gameData.SpawnAreaPoints);
    }

    [Fact]
    public void Discovery_Finds_Root_And_Page_Files()
    {
        var discovery = new IslandFileDiscovery();

        var entries = new List<ArchiveEntryIndex>
        {
            new() { RelativePath = "moderate_s_02__interpreted/moderate_s_02.xml" },
            new() { RelativePath = "moderate_s_02__interpreted/moderate_s_02/rd3d.xml" },
            new() { RelativePath = "moderate_s_02__interpreted/moderate_s_02/gamedata.xml" },
            new() { RelativePath = "moderate_s_02__interpreted/_gamedata/moderate_s_02/meshes/0/2x3.xml" },
            new() { RelativePath = "moderate_s_02__interpreted/_gamedata/moderate_s_02/normalmaps/0/2x3.xml" },
            new() { RelativePath = "moderate_s_02__interpreted/_gamedata/moderate_s_02/tintmaps/0/0x0.dds" }
        };

        var discovered = discovery.Discover(entries);

        Assert.Equal("moderate_s_02__interpreted/moderate_s_02.xml", discovered.RootIslandXmlPath);
        Assert.Single(discovered.MeshPagePaths);
        Assert.Single(discovered.NormalMapPagePaths);
        Assert.Single(discovered.TintMapPagePaths);
    }

    public void DimensionValidator_Returns_No_Errors_For_Consistent_Minimal_Project()
    {
        var project = new IslandProject
        {
            Root = new IslandRootDefinition
            {
                MapSize = 192,
                ActiveMapGrid = new(24, 24)
            },
            Metadata = new IslandMetadata
            {
                MapSize = 192
            },
            Rd3d = new Rd3dDefinition
            {
                GridWidth = 6144,
                GridHeight = 6144,
                MaterialMapWidth = 384,
                MaterialMapHeight = 384,
                TintMapWidth = 768,
                TintMapHeight = 768
            },
            GameData = new GameDataDefinition
            {
                TerrainHeightMap = new() { Width = 385, Height = 385 }
            }
        };

        var report = new IslandValidator().Validate(project);

        Assert.False(report.HasErrors);
    }

    [Fact]
    public void Editor_Updates_Metadata_Syncs_Dimensions_And_Regenerates_Texts()
    {
        var project = new IslandProject
        {
            Metadata = new IslandMetadata { IslandInternalName = "old", MapSize = 192, ActiveMapGrid = new Int2(24, 24), ActiveMapRect = new RectI(24, 16, 176, 168) },
            Root = new IslandRootDefinition { MapSize = 192, ActiveMapGrid = new Int2(24, 24), ActiveMapRect = new RectI(24, 16, 176, 168) },
            Rd3d = new Rd3dDefinition { GridWidth = 6144, GridHeight = 6144, MaterialMapWidth = 384, MaterialMapHeight = 384, TintMapWidth = 768, TintMapHeight = 768 },
            GameData = new GameDataDefinition { TerrainHeightMap = new() { Width = 385, Height = 385 } },
            RawContent = new RawProjectContent
            {
                RootText = "<Content><MapSize>192 192</MapSize><ActiveMapGrid><x>24</x><y>24</y></ActiveMapGrid><ActiveMapRect>24 16 176 168</ActiveMapRect><ObjectMetaInfo><SlotObjects></SlotObjects></ObjectMetaInfo></Content>",
                Rd3dText = "<Content><GridWidth>6144</GridWidth><GridHeight>6144</GridHeight><MaterialMapWidth>384</MaterialMapWidth><MaterialMapHeight>384</MaterialMapHeight><TintMapWidth>768</TintMapWidth><TintMapHeight>768</TintMapHeight></Content>",
                GameDataText = "<Content><TerrainManager><WorldSize>192 192</WorldSize><HeightMap><Width>385</Width><Height>385</Height></HeightMap></TerrainManager></Content>"
            }
        };

        var editor = new IslandProjectEditor();
        editor.ApplyMetadataEdits(project, new MetadataEditModel
        {
            IslandInternalName = "new_name",
            MapSize = 320,
            ActiveMapGrid = new Int2(40, 40),
            ActiveMapRect = new RectI(0, 8, 312, 304),
            MaterialSetFileName = "terra_01_st.xml"
        }, syncDerivedDimensions: true);

        Assert.Equal(320, project.Root.MapSize);
        Assert.Equal(10240, project.Rd3d.GridWidth);
        Assert.Equal(641, project.GameData.TerrainHeightMap.Width);
        Assert.Contains("terra_01_st.xml", project.RawContent.Rd3dText);
        Assert.True(project.IsModified);
    }

    [Fact]
    public void Editor_Replaces_Slots_Into_Raw_Root_Text()
    {
        var project = new IslandProject
        {
            Root = new IslandRootDefinition(),
            RawContent = new RawProjectContent
            {
                RootText = "<Content><MapSize>192 192</MapSize><ActiveMapGrid><x>24</x><y>24</y></ActiveMapGrid><ActiveMapRect>24 16 176 168</ActiveMapRect><ObjectMetaInfo><SlotObjects></SlotObjects></ObjectMetaInfo></Content>"
            }
        };

        var editor = new IslandProjectEditor();
        editor.ReplaceSlotObjects(project, new List<SlotObjectDefinition>
        {
            new() { ObjectId = 1, ObjectGuid = "1001", Position = new Float3(1, 2, 3) }
        });

        Assert.Single(project.Root.SlotObjects);
        Assert.Contains("<ObjectGuid>1001</ObjectGuid>", project.RawContent.RootText);
    }


    [Fact]
    public void Editor_Replaces_Slots_Removes_Deleted_None_Nodes_From_Raw_Root_Text()
    {
        const string rawRoot = """
<Content>
  <MapSize>192 192</MapSize>
  <ActiveMapGrid><x>24</x><y>24</y></ActiveMapGrid>
  <ActiveMapRect>24 16 176 168</ActiveMapRect>
  <ObjectMetaInfo>
    <SlotObjects>
      <None>
        <value>
          <id>2</id>
        </value>
      </None>
      <None>
        <None>
          <ObjectId>-9199693229316898788</ObjectId>
          <ObjectGuid>1000253</ObjectGuid>
          <Position>249,2297 11,17881 207,5638</Position>
        </None>
        <None>
          <ObjectId>42</ObjectId>
          <ObjectGuid>2000001</ObjectGuid>
          <Position>1,0 2,0 3,0</Position>
        </None>
      </None>
    </SlotObjects>
  </ObjectMetaInfo>
</Content>
""";

        var project = new IslandProject
        {
            Root = new IslandRootDefinition(),
            RawContent = new RawProjectContent
            {
                RootText = rawRoot
            }
        };

        var editor = new IslandProjectEditor();
        editor.ReplaceSlotObjects(project, new List<SlotObjectDefinition>
        {
            new() { ObjectId = 42, ObjectGuid = "2000001", Position = new Float3(1, 2, 3) }
        });

        Assert.DoesNotContain("-9199693229316898788", project.RawContent.RootText);
        Assert.Contains("<ObjectId>42</ObjectId>", project.RawContent.RootText);
        // Preserve category 2 from the input; the id is not the slot count.
        Assert.Contains("<id>2</id>", project.RawContent.RootText);
        Assert.Equal(1, Regex.Matches(project.RawContent.RootText, @"<ObjectId>").Count);
    }

    [Fact]
    public void Editor_Replaces_Slots_Collapses_Duplicate_SlotObjects_Blocks()
    {
        const string rawRoot = "<Content><MapSize>192 192</MapSize><ActiveMapGrid><x>24</x><y>24</y></ActiveMapGrid><ActiveMapRect>0 0 192 192</ActiveMapRect><ObjectMetaInfo><SlotObjects><None /></SlotObjects><SlotObjects><None /></SlotObjects><SlotObjects><None /></SlotObjects></ObjectMetaInfo></Content>";
        var project = new IslandProject
        {
            Root = new IslandRootDefinition(),
            RawContent = new RawProjectContent { RootText = rawRoot }
        };

        new IslandProjectEditor().ReplaceSlotObjects(project, new List<SlotObjectDefinition>
        {
            new() { ObjectId = 7, ObjectGuid = "1000253", Position = new Float3(8, 1, 8) }
        });

        Assert.Equal(1, Regex.Matches(project.RawContent.RootText, @"<SlotObjects>").Count);
        Assert.Equal(1, Regex.Matches(project.RawContent.RootText, @"<ObjectId>").Count);
        Assert.Contains("<ObjectId>7</ObjectId>", project.RawContent.RootText);
    }

    public void ConservativeExporter_Copies_Source_Archive_And_Adds_Report()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Anno1800IslandToolTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var sourceZip = Path.Combine(tempDir, "source.zip");
        using (var archive = ZipFile.Open(sourceZip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("test.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("hello");
        }

        var project = new IslandProject
        {
            Metadata = new IslandMetadata { SourceArchivePath = sourceZip, ProjectName = "test" },
            Root = new IslandRootDefinition(),
            ValidationReport = new()
        };

        var outputZip = Path.Combine(tempDir, "export.zip");
        var report = new IslandExporter().Export(project, outputZip, new ExportOptions());

        Assert.True(report.Succeeded);
        Assert.True(File.Exists(outputZip));
        using var exported = ZipFile.OpenRead(outputZip);
        Assert.NotNull(exported.GetEntry("test.txt"));
        Assert.NotNull(exported.GetEntry("_anno1800islandtool/validation-report.txt"));
    }
    [Fact]
    public void LocalTerrainDerivativeRegenerator_Builds_Synthetic_Previews_And_Affected_Pages()
    {
        var map = new CoarseHeightMap
        {
            Width = 9,
            Height = 9,
            Samples = Enumerable.Range(0, 81).Select(i => (float)i).ToArray()
        };

        var project = new IslandProject
        {
            GameData = new GameDataDefinition { TerrainHeightMap = map },
            MeshPages = new Dictionary<TerrainPageKey, MeshPage>
            {
                [new TerrainPageKey(0, 0, 0)] = new MeshPage { Key = new TerrainPageKey(0, 0, 0), OriginalRelativePath = "mesh/0/0x0.xml" },
                [new TerrainPageKey(0, 1, 0)] = new MeshPage { Key = new TerrainPageKey(0, 1, 0), OriginalRelativePath = "mesh/0/1x0.xml" },
                [new TerrainPageKey(0, 0, 1)] = new MeshPage { Key = new TerrainPageKey(0, 0, 1), OriginalRelativePath = "mesh/0/0x1.xml" },
                [new TerrainPageKey(0, 1, 1)] = new MeshPage { Key = new TerrainPageKey(0, 1, 1), OriginalRelativePath = "mesh/0/1x1.xml" }
            },
            NormalMapPages = new Dictionary<TerrainPageKey, NormalMapPage>
            {
                [new TerrainPageKey(0, 0, 0)] = new NormalMapPage { Key = new TerrainPageKey(0, 0, 0), OriginalRelativePath = "normal/0/0x0.xml" }
            },
            TintMapPages = new Dictionary<TerrainPageKey, TintMapPage>
            {
                [new TerrainPageKey(0, 0, 0)] = new TintMapPage { Key = new TerrainPageKey(0, 0, 0), OriginalRelativePath = "tint/0/0x0.dds", Width = 64, Height = 64 }
            }
        };

        var regenerator = new LocalTerrainDerivativeRegenerator();
        var state = regenerator.RegenerateAroundPage(project, map, "Mesh", new TerrainPageKey(0, 0, 0), 1);

        Assert.True(state.HasData);
        Assert.True(state.HeightPreview.HasPixels);
        Assert.True(state.NormalPreview.HasPixels);
        Assert.True(state.TintPreview.HasPixels);
        Assert.NotEmpty(state.AffectedPages);
    }

    [Fact]
    public void NormalPreview_V4617_Refreshes_From_Authoritative_Map_Before_Export()
    {
        var original = new CoarseHeightMap
        {
            Width = 9,
            Height = 9,
            Samples = new float[81]
        };
        var authoritative = new CoarseHeightMap
        {
            Width = 9,
            Height = 9,
            Samples = Enumerable.Range(0, 81)
                .Select(index => (float)((index % 9) * (index / 9)))
                .ToArray()
        };
        var project = new IslandProject
        {
            GameData = new GameDataDefinition { TerrainHeightMap = original }
        };
        var regenerator = new LocalTerrainDerivativeRegenerator();
        var bounds = new RectI(0, 0, 8, 8);
        var state = regenerator.RegenerateAroundBounds(project, original, "Mesh", null, bounds, bounds, 0);
        var before = state.NormalPreview.PixelsBgra32.ToArray();

        regenerator.GenerateInterpretedNormalMapWrites(project, authoritative, state);

        Assert.True(state.NormalPreview.HasPixels);
        Assert.False(before.SequenceEqual(state.NormalPreview.PixelsBgra32));
    }



    public void Exporter_Adds_Generated_Terrain_Entries_When_Missing_From_Source()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Anno1800IslandToolTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var sourceZip = Path.Combine(tempDir, "source.zip");
        using (var archive = ZipFile.Open(sourceZip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("root/test.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("hello");
        }

        var project = new IslandProject
        {
            Metadata = new IslandMetadata { SourceArchivePath = sourceZip, ProjectName = "test", IslandInternalName = "island_a", OriginalIslandInternalName = "island_a" },
            ValidationReport = new(),
            LocalRegeneration = new TerrainLocalRegenerationState
            {
                Summary = "regen",
                GeneratedMeshPageWrites =
                {
                    new RegeneratedMeshPageWrite
                    {
                        Key = new TerrainPageKey(4, 1, 2),
                        TargetPath = "_gamedata/island_a/meshes/4/1x2.xml",
                        XmlText = "<MeshPage/>",
                        Summary = "mesh"
                    }
                },
                GeneratedNormalMapWrites =
                {
                    new RegeneratedNormalMapWrite
                    {
                        Key = new TerrainPageKey(4, 1, 2),
                        TargetPath = "_gamedata/island_a/normalmaps/4/1x2.xml",
                        XmlText = "<NormalMapPage/>",
                        Summary = "normal"
                    }
                }
            }
        };

        var outputZip = Path.Combine(tempDir, "export.zip");
        var report = new IslandExporter().Export(project, outputZip, new ExportOptions());

        Assert.True(report.Succeeded);
        using var exported = ZipFile.OpenRead(outputZip);
        Assert.NotNull(exported.GetEntry("_gamedata/island_a/meshes/4/1x2.xml"));
        Assert.NotNull(exported.GetEntry("_gamedata/island_a/normalmaps/4/1x2.xml"));
        Assert.NotNull(exported.GetEntry("_anno1800islandtool/export-manifest.txt"));
    }


    public void Exporter_Writes_Generated_TintMaps_In_Full_Export()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Anno1800IslandToolTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var sourceZip = Path.Combine(tempDir, "source.zip");
        using (var archive = ZipFile.Open(sourceZip, ZipArchiveMode.Create))
        {
            var sourceEntry = archive.CreateEntry("_gamedata/island_a/tintmaps/4/1x2.dds");
            using var sourceStream = sourceEntry.Open();
            sourceStream.Write(new byte[] { 0x01, 0x02, 0x03 }, 0, 3);
        }

        var project = new IslandProject
        {
            Metadata = new IslandMetadata { SourceArchivePath = sourceZip, ProjectName = "test", IslandInternalName = "island_a", OriginalIslandInternalName = "island_a" },
            ValidationReport = new(),
            LocalRegeneration = new TerrainLocalRegenerationState
            {
                GeneratedTintMapWrites =
                {
                    new RegeneratedTintMapWrite
                    {
                        Key = new TerrainPageKey(4, 1, 2),
                        TargetPath = "_gamedata/island_a/tintmaps/4/1x2.dds",
                        DdsBytes = new byte[] { 0x44, 0x44, 0x53, 0x20 },
                        Width = 1,
                        Height = 1,
                        Summary = "tint"
                    }
                }
            }
        };

        var outputZip = Path.Combine(tempDir, "export.zip");
        var report = new IslandExporter().Export(project, outputZip, new ExportOptions());

        Assert.True(report.Succeeded);
        using var exported = ZipFile.OpenRead(outputZip);
        var exportedEntry = exported.GetEntry("_gamedata/island_a/tintmaps/4/1x2.dds");
        Assert.NotNull(exportedEntry);
        using var exportedStream = exportedEntry!.Open();
        using var memory = new MemoryStream();
        exportedStream.CopyTo(memory);
        Assert.Equal(new byte[] { 0x44, 0x44, 0x53, 0x20 }, memory.ToArray());
    }

    [Fact]
    public void Validator_Flags_Xml_Declaration_And_Missing_Gameplay_Blocks()
    {
        var project = new IslandProject
        {
            Metadata = new IslandMetadata { RootIslandFileName = "island.xml" },
            RootFilePath = "island.xml",
            Rd3dFilePath = "rd3d.xml",
            GameDataFilePath = "gamedata.xml",
            SourceFiles = new List<FileReference>
            {
                new() { RelativePath = "island.xml", Kind = FileKind.RootIslandXml },
                new() { RelativePath = "rd3d.xml", Kind = FileKind.Rd3dXml },
                new() { RelativePath = "gamedata.xml", Kind = FileKind.GameDataXml }
            },
            Root = new IslandRootDefinition { MapSize = 192, ActiveMapGrid = new Int2(24, 24) },
            Rd3d = new Rd3dDefinition { GridWidth = 6144, GridHeight = 6144, MaterialMapWidth = 384, MaterialMapHeight = 384, TintMapWidth = 768, TintMapHeight = 768, NodeSize = 32, UnitScale = 0.03125f, PageSize = 512, TintPageSize = 512, NodesPerMeshCluster = 32, MinMeshLevel = 2, UsingSplitImageHeightMaps = true, HeightPageBorderTexels = 8 },
            GameData = new GameDataDefinition { TerrainHeightMap = new CoarseHeightMap { Width = 385, Height = 385 } },
            RawContent = new RawProjectContent
            {
                RootText = "<?xml version=\"1.0\"?><Content></Content>",
                Rd3dText = "<Content></Content>",
                GameDataText = "<GameData><TerrainManager><HeightMap><Width>385</Width><Height>385</Height></HeightMap></TerrainManager></GameData>"
            }
        };

        var report = new IslandValidator().Validate(project);

        Assert.Contains(report.Messages, m => m.Code == ValidationCode.XmlDeclarationNotSupported);
        Assert.Contains(report.Messages, m => m.Code == ValidationCode.MissingGameplayManager);
    }

    [Fact]
    public void GameDataReader_Reads_Advanced_Gameplay_Block_Summaries()
    {
        const string text = @"<GameData>
  <TerrainManager><HeightMap><Width>385</Width><Height>385</Height><HeightMap>1 2 3</HeightMap></HeightMap></TerrainManager>
  <PlayableArea>0 1 10 20</PlayableArea>
  <RegrowManager><TreeMap><x>384</x><y>384</y><bits>0 1 0 2</bits></TreeMap></RegrowManager>
  <AreaIDs><x>384</x><y>384</y><val>0 1 2 2 0</val></AreaIDs>
  <SpawnAreaPoints><None><Position>1 2 3</Position></None></SpawnAreaPoints>
</GameData>";

        var data = new GameDataReader().Read(text);

        Assert.True(data.GameplayBlocks["RegrowManager"].IsPresent);
        Assert.Equal(4, data.GameplayBlocks["RegrowManager"].BitCount);
        Assert.Equal(2, data.Areas.Count);
        Assert.Single(data.SpawnAreaPoints);
    }

    [Fact]
    public void ProjectEditor_ReplaceZones_Updates_Model_And_Simple_GameData_Text()
    {
        var project = new IslandProject
        {
            Metadata = new IslandMetadata { MapSize = 192 },
            GameData = new GameDataDefinition
            {
                TerrainHeightMap = new CoarseHeightMap { Width = 385, Height = 385, Samples = new float[] { 1, 2, 3 } },
                PlayableArea = new RectI(0, 0, 1, 1)
            },
            RawContent = new RawProjectContent
            {
                GameDataText = "<GameData><TerrainManager><HeightMap><Width>385</Width><Height>385</Height><HeightMap>1 2 3</HeightMap></HeightMap></TerrainManager><PlayableArea>0 0 1 1</PlayableArea></GameData>"
            },
            Rd3d = new Rd3dDefinition { MaterialMapWidth = 384, MaterialMapHeight = 384 },
            ValidationReport = new()
        };

        new IslandProjectEditor().ReplaceZones(
            project,
            new RectI(1, 2, 3, 4),
            new[] { new AreaDefinition { Id = "7" } },
            new[] { new SpawnAreaPointDefinition { Position = new Float3(10, 11, 12), Tag = "test" } });

        Assert.Equal(1, project.GameData.Areas.Count);
        Assert.Single(project.GameData.SpawnAreaPoints);
        Assert.Contains("<AreaIDs>", project.RawContent.GameDataText);
        Assert.Contains("<SpawnAreaPoints>", project.RawContent.GameDataText);
    }

    [Fact]
    public void TerrainHeightEncoding_Separates_GameData_Mesh_And_Normal_Raw_Heights()
    {
        Assert.True(Math.Abs(TerrainHeightEncoding.GameDataRawToWorldHeight(64512) - (-8f)) < 0.001f);
        Assert.Equal(64512, TerrainHeightEncoding.WorldHeightToGameDataRaw(-8f));

        Assert.True(Math.Abs(TerrainHeightEncoding.MeshRawToWorldHeight(30720) - (-8f)) < 0.001f);
        Assert.Equal(30720, TerrainHeightEncoding.WorldHeightToMeshRaw(-8f));

        Assert.True(Math.Abs(TerrainHeightEncoding.MeshRawToWorldHeight(30720) - TerrainHeightEncoding.NormalPageRawToWorldHeight(30720)) < 0.001f);
    }


    [Fact]
    public void TerrainConsistencyValidator_Produces_V21_2_Diagnostic_Messages()
    {
        var project = new IslandProject
        {
            Metadata = new IslandMetadata
            {
                ProjectName = "diagnostic_test",
                IslandInternalName = "moderate_s_02",
                SourceArchivePath = "moderate_s_02__interpreted.zip"
            },
            Root = new IslandRootDefinition
            {
                MapSize = 192,
                ActiveMapGrid = new Int2(24, 24),
                ActiveMapRect = new RectI(24, 16, 176, 168)
            },
            Rd3d = new Rd3dDefinition
            {
                GridWidth = 6144,
                GridHeight = 6144,
                MaterialMapWidth = 384,
                MaterialMapHeight = 384,
                TintMapWidth = 768,
                TintMapHeight = 768,
                TintPageSize = 512
            },
            GameData = new GameDataDefinition
            {
                TerrainHeightMap = new CoarseHeightMap
                {
                    Width = 385,
                    Height = 385,
                    Samples = Enumerable.Repeat(64512f, 385 * 385).ToArray(),
                    SampleEncoding = TerrainHeightSampleEncoding.GameDataRawUInt16
                },
                PlayableArea = new RectI(24, 16, 176, 168)
            },
            MaterialSet = new MaterialSetDefinition
            {
                FileName = "terra_01.xml",
                Materials = new List<MaterialDefinition>
                {
                    new() { Id = 1, Type = "0", FileName = "data/graphics/terrain/beach.prp" },
                    new() { Id = 32, Type = "1", ObjectFileName = "data/graphics/props/grass.prp" }
                }
            },
            EffectiveMaterialSetName = "terra_01.xml"
        };

        project.MeshPages[new TerrainPageKey(0, 0, 0)] = new MeshPage { Key = new TerrainPageKey(0, 0, 0), OriginalRelativePath = "_gamedata/moderate_s_02/meshes/0/0x0.xml" };
        project.MeshPages[new TerrainPageKey(2, 0, 0)] = new MeshPage
        {
            Key = new TerrainPageKey(2, 0, 0),
            OriginalRelativePath = "_gamedata/moderate_s_02/meshes/2/0x0.xml",
            HasEditablePayload = true,
            Clusters = new List<MeshCluster>
            {
                new() { Vertices = new List<MeshVertex> { new() { Raw1 = TerrainHeightEncoding.WorldHeightToMeshRaw(-8f) } } }
            }
        };
        project.NormalMapPages[new TerrainPageKey(2, 0, 0)] = new NormalMapPage
        {
            Key = new TerrainPageKey(2, 0, 0),
            OriginalRelativePath = "_gamedata/moderate_s_02/normalmaps/2/0x0.xml",
            DownsampledWidth = 17,
            DownsampledHeight = 17,
            DownsampledSamples = new[] { TerrainHeightEncoding.WorldHeightToNormalPageRaw(-8f) }
        };
        project.TintMapPages[new TerrainPageKey(0, 0, 0)] = new TintMapPage
        {
            Key = new TerrainPageKey(0, 0, 0),
            OriginalRelativePath = "_gamedata/moderate_s_02/tintmaps/0/0x0.dds",
            Width = 512,
            Height = 512
        };

        var report = new TerrainConsistencyValidator().Validate(project);

        Assert.Contains(report.Messages, m => m.Code == ValidationCode.TerrainDiagnostic && m.Message.Contains("Diagnostic terrain v30"));
        Assert.Contains(report.Messages, m => m.Code == ValidationCode.TerrainHeightEncoding && m.Message.Contains("[OK] HeightMap GameData"));
        Assert.Contains(report.Messages, m => m.Code == ValidationCode.TerrainDiagnostic && m.Message.Contains("LOD0/LOD1 protég"));
        Assert.Contains(report.Messages, m => m.Code == ValidationCode.TerrainDiagnostic && m.Message.Contains("MaterialSet chargé"));
    }


    [Fact]
    public void TerrainPageCoverage_Uses_Relative_Grid_For_Cropped_Normalmaps()
    {
        var project = new IslandProject
        {
            Root = new IslandRootDefinition { MapSize = 64 },
            GameData = new GameDataDefinition
            {
                TerrainHeightMap = new CoarseHeightMap { Width = 129, Height = 129 }
            }
        };

        project.MeshPages[new TerrainPageKey(2, 0, 0)] = new MeshPage { Key = new TerrainPageKey(2, 0, 0), HasEditablePayload = true };
        project.MeshPages[new TerrainPageKey(2, 1, 0)] = new MeshPage { Key = new TerrainPageKey(2, 1, 0), HasEditablePayload = true };
        project.NormalMapPages[new TerrainPageKey(2, 1, 0)] = new NormalMapPage { Key = new TerrainPageKey(2, 1, 0) };
        project.NormalMapPages[new TerrainPageKey(2, 2, 0)] = new NormalMapPage { Key = new TerrainPageKey(2, 2, 0) };

        var coverage = TerrainPageCoverage.BuildProjectCoverage(project);
        var mesh00 = TerrainPageCoverage.Find(coverage, TerrainPageCoverage.MeshFamily, new TerrainPageKey(2, 0, 0));
        var normal10 = TerrainPageCoverage.Find(coverage, TerrainPageCoverage.NormalMapFamily, new TerrainPageKey(2, 1, 0));

        Assert.NotNull(mesh00);
        Assert.NotNull(normal10);
        Assert.Equal(mesh00!.SampleBounds, normal10!.SampleBounds);
        Assert.True(TerrainPageCoverage.FindOverlapping(coverage, TerrainPageCoverage.NormalMapFamily, mesh00.SampleBounds, 2).Any());
    }

    [Fact]
    public void TerrainCoverageValidator_Does_Not_Require_Identical_Page_Keys()
    {
        var project = new IslandProject
        {
            Root = new IslandRootDefinition { MapSize = 64 },
            GameData = new GameDataDefinition
            {
                TerrainHeightMap = new CoarseHeightMap { Width = 129, Height = 129 }
            }
        };

        project.MeshPages[new TerrainPageKey(2, 0, 0)] = new MeshPage { Key = new TerrainPageKey(2, 0, 0), HasEditablePayload = true };
        project.MeshPages[new TerrainPageKey(2, 1, 0)] = new MeshPage { Key = new TerrainPageKey(2, 1, 0), HasEditablePayload = true };
        project.NormalMapPages[new TerrainPageKey(2, 1, 0)] = new NormalMapPage { Key = new TerrainPageKey(2, 1, 0) };
        project.NormalMapPages[new TerrainPageKey(2, 2, 0)] = new NormalMapPage { Key = new TerrainPageKey(2, 2, 0) };

        var report = new TerrainCoverageValidator().Validate(project);

        Assert.Contains(report.Messages, m => m.Code == ValidationCode.TerrainCoverageDiagnostic && m.Message.Contains("recouvrement spatial"));
        Assert.False(report.Messages.Any(m => m.Severity == ValidationSeverity.Warning && m.Code == ValidationCode.MissingTerrainCounterpart));
    }


    [Fact]
    public void TerrainMaterialPackEncoding_Decodes_And_Reencodes_Shader_Observed_Value()
    {
        var ids = TerrainMaterialPackEncoding.DecodeMaterialIds(936311777u);

        Assert.Equal(new[] { 1, 31, 30, 29, 28, 27 }, ids);
        Assert.Equal(936311777u, TerrainMaterialPackEncoding.EncodeMaterialIds(ids));
        Assert.Equal(1073741823u, TerrainMaterialPackEncoding.EncodeAlphaWeights(new[] { 31, 31, 31, 31, 31, 31 }));
        Assert.Equal(new[] { 31, 31, 31, 31, 31, 31 }, TerrainMaterialPackEncoding.DecodeAlphaWeights(1073741823u));
    }

    [Fact]
    public void Rd3dReader_Reads_MaterialIDs2_And_MaterialAlphas2()
    {
        const string rd3dText = """
<RD3D>
  <MaterialMapWidth>4</MaterialMapWidth>
  <MaterialMapHeight>2</MaterialMapHeight>
  <MaterialIDBlockSize>2</MaterialIDBlockSize>
  <MaterialIDs2>936311777 970948225</MaterialIDs2>
  <MaterialAlphas2>31 1023 32767 1048575 1073741823 31 31 31</MaterialAlphas2>
</RD3D>
""";

        var rd3d = new Rd3dReader().Read(rd3dText);

        Assert.Equal(4, rd3d.MaterialMapWidth);
        Assert.Equal(2, rd3d.MaterialMapHeight);
        Assert.Equal(2, rd3d.MaterialIDBlockSize);
        Assert.Equal(2, rd3d.MaterialMap.PackedMaterialIds.Length);
        Assert.Equal(8, rd3d.MaterialMap.PackedMaterialAlphas.Length);
        Assert.Equal(new[] { 1, 31, 30, 29, 28, 27 }, TerrainMaterialPackEncoding.DecodeMaterialIds(rd3d.MaterialMap.PackedMaterialIds[0]));
        Assert.Equal(new[] { 31, 31, 31, 31, 31, 31 }, TerrainMaterialPackEncoding.DecodeAlphaWeights(rd3d.MaterialMap.PackedMaterialAlphas[4]));
    }

    [Fact]
    public void TerrainMaterialMap_Returns_Dominant_Material_For_Cell()
    {
        var map = new TerrainMaterialMap
        {
            Width = 2,
            Height = 2,
            MaterialIdBlockSize = 2,
            PackedMaterialIds = new[] { TerrainMaterialPackEncoding.EncodeMaterialIds(new[] { 1, 2, 3, 4, 5, 6 }) },
            PackedMaterialAlphas = new[]
            {
                TerrainMaterialPackEncoding.EncodeAlphaWeights(new[] { 0, 31, 0, 0, 0, 0 }),
                TerrainMaterialPackEncoding.EncodeAlphaWeights(new[] { 0, 0, 31, 0, 0, 0 }),
                TerrainMaterialPackEncoding.EncodeAlphaWeights(new[] { 0, 0, 0, 31, 0, 0 }),
                TerrainMaterialPackEncoding.EncodeAlphaWeights(new[] { 0, 0, 0, 0, 31, 0 })
            }
        };

        Assert.Equal(2, map.GetDominantMaterialId(0, 0));
        Assert.Equal(3, map.GetDominantMaterialId(1, 0));
        Assert.Equal(4, map.GetDominantMaterialId(0, 1));
        Assert.Equal(5, map.GetDominantMaterialId(1, 1));
    }

    [Fact]
    public void TerrainMaterialMapValidator_Checks_Packed_Array_Counts()
    {
        var project = new IslandProject
        {
            Rd3dFilePath = "rd3d.xml",
            Rd3d = new Rd3dDefinition
            {
                MaterialMapWidth = 4,
                MaterialMapHeight = 2,
                MaterialIDBlockSize = 2,
                MaterialMap = new TerrainMaterialMap
                {
                    Width = 4,
                    Height = 2,
                    MaterialIdBlockSize = 2,
                    PackedMaterialIds = new[] { 936311777u, 970948225u },
                    PackedMaterialAlphas = new[] { 31u, 1023u, 32767u, 1048575u, 1073741823u, 31u, 31u, 31u }
                }
            },
            MaterialSet = new MaterialSetDefinition
            {
                Materials = new List<MaterialDefinition>
                {
                    new() { Id = 1 },
                    new() { Id = 28 },
                    new() { Id = 29 },
                    new() { Id = 30 }
                }
            }
        };

        var report = new TerrainMaterialMapValidator().Validate(project);

        Assert.Contains(report.Messages, m => m.Code == ValidationCode.MaterialMapEncoding && m.Message.Contains("[OK] MaterialIDs2"));
        Assert.Contains(report.Messages, m => m.Code == ValidationCode.MaterialMapEncoding && m.Message.Contains("[OK] MaterialAlphas2"));
    }

    [Fact]
    public void MaterialSetWriter_WritePreserving_Keeps_PseudoXml_Unknown_Fields()
    {
        const string original = """
<MaterialSet><ShaderType>0</>
<Material><ID>1</>
<Type>0</>
<FileName>old.xml</>
<Repetition>4.000000</>
<AlphaSharpness>5.000000</>
<GroundType>None</>
<ObjectFileName></>
<GritEnabled>1</>
</>
<Material><ID>2</>
<Type>0</>
<FileName>other.xml</>
<GritEnabled>0</>
</>
</>
""";

        var set = new MaterialSetDefinition
        {
            FileName = "terra_01.xml",
            Materials =
            {
                new()
                {
                    Id = 1,
                    Type = "0",
                    FileName = "new.xml",
                    Repetition = 12.5f,
                    AlphaSharpness = 5f,
                    GroundType = "None",
                    ObjectFileName = string.Empty
                },
                new() { Id = 2, Type = "0", FileName = "other.xml" }
            }
        };

        var written = new MaterialSetWriter().WritePreserving(set, original);

        Assert.Contains("<ShaderType>0</>", written);
        Assert.Contains("<FileName>new.xml</>", written);
        Assert.Contains("<Repetition>12.5</>", written);
        Assert.Contains("<GritEnabled>1</>", written);
        Assert.Contains("<FileName>other.xml</>", written);
        Assert.DoesNotContain("<Materials>", written);
    }

    [Fact]
    public void ProjectTextRegenerator_Regenerates_MaterialSet_Without_Destroying_PseudoXml()
    {
        var project = new IslandProject
        {
            MaterialSet = new MaterialSetDefinition
            {
                FileName = "terra_01.xml",
                Materials =
                {
                    new()
                    {
                        Id = 1,
                        Type = "0",
                        FileName = "data/new.xml",
                        Repetition = 4f,
                        AlphaSharpness = 5f,
                        GroundType = "None",
                        ObjectFileName = string.Empty
                    }
                }
            },
            RawContent = new RawProjectContent
            {
                MaterialSetText = "<MaterialSet><ShaderType>0</><Material><ID>1</><Type>0</><FileName>data/old.xml</><GritEnabled>1</></></>"
            }
        };

        new ProjectTextRegenerator().RegenerateMaterialSetText(project);

        Assert.Contains("<ShaderType>0</>", project.RawContent.MaterialSetText);
        Assert.Contains("<FileName>data/new.xml</>", project.RawContent.MaterialSetText);
        Assert.Contains("<GritEnabled>1</>", project.RawContent.MaterialSetText);
        Assert.DoesNotContain("<Materials>", project.RawContent.MaterialSetText);
    }

    public void NormalMapPageWriter_WritePreserving_Replaces_Only_Map_Values()
    {
        const string original = """
<Content>
  <HeightMap>
    <Downsampled><width>2</width><height>1</height><map>1 2</map></Downsampled>
    <Deltas>
      <None><Data><width>2</width><height>1</height><map>3 4</map></Data><ScaleFactor>1</ScaleFactor><QuantBits>8</QuantBits><Extra>keep</Extra></None>
    </Deltas>
  </HeightMap>
</Content>
""";

        var page = new NormalMapPage
        {
            DownsampledWidth = 2,
            DownsampledHeight = 1,
            RawUnknownFields = { ["__raw"] = original },
            DeltaLevels =
            {
                new QuantizedDeltaLevel { Width = 2, Height = 1, ScaleFactor = 1, QuantBits = 8 }
            }
        };

        var written = new NormalMapPageWriter().WritePreserving(page, "10 20", new[] { "30 40" });

        Assert.Contains("<map>10 20</map>", written);
        Assert.Contains("<map>30 40</map>", written);
        Assert.Contains("<Extra>keep</Extra>", written);
    }

    [Fact]
    public void MeshPageClassifier_Protects_Lod0_Lod1_As_Metadata()
    {
        var page = new MeshPage
        {
            Key = new TerrainPageKey(1, 0, 0),
            HasEditablePayload = true,
            HasDecodedPayload = true
        };
        page.Clusters.Add(new MeshCluster
        {
            Vertices = { new MeshVertex { RawValues = new[] { 0, 32767, 0, 0, 0, 0, 0, 0 } } },
            GroundObjects = { new GroundObjectInstance { ObjectFileName = "data/ground/test.cfg" } }
        });

        Assert.Equal(MeshPageRole.MetadataCoverage, MeshPageClassifier.Classify(page.Key, page));
        Assert.False(MeshPageClassifier.CanEditGeometry(page));
        Assert.Contains("protégé", MeshPageClassifier.BuildReason(page.Key, page));
    }

    [Fact]
    public void TerrainCoverageValidator_Does_Not_Require_Normalmap_For_Protected_Low_Lods()
    {
        var project = new IslandProject
        {
            Root = new IslandRootDefinition { MapSize = 64 },
            GameData = new GameDataDefinition
            {
                TerrainHeightMap = new CoarseHeightMap { Width = 129, Height = 129 }
            }
        };

        var lowLod = new MeshPage
        {
            Key = new TerrainPageKey(1, 0, 0),
            HasEditablePayload = true,
            HasDecodedPayload = true
        };
        lowLod.Clusters.Add(new MeshCluster
        {
            Vertices = { new MeshVertex { RawValues = new[] { 0, 32767, 0, 0, 0, 0, 0, 0 } } }
        });
        project.MeshPages[lowLod.Key] = lowLod;

        var report = new TerrainCoverageValidator().Validate(project);

        Assert.Contains(report.Messages, m => m.Code == ValidationCode.TerrainCoverageDiagnostic && m.Message.Contains("LOD0/LOD1"));
        Assert.DoesNotContain(report.Messages, m => m.Severity == ValidationSeverity.Warning && m.Code == ValidationCode.MissingTerrainCounterpart);
    }

    [Fact]
    public void TerrainMaterialMapCodec_Decodes_And_Encodes_Full_Map_Cells()
    {
        var map = new TerrainMaterialMap
        {
            Width = 2,
            Height = 2,
            MaterialIdBlockSize = 2,
            PackedMaterialIds = new[] { TerrainMaterialPackEncoding.EncodeMaterialIds(new[] { 1, 2, 3, 4, 5, 6 }) },
            PackedMaterialAlphas = new[]
            {
                TerrainMaterialPackEncoding.EncodeMaterialAlphas(new[] { 31, 0, 0, 0, 0, 0 }),
                TerrainMaterialPackEncoding.EncodeMaterialAlphas(new[] { 0, 31, 0, 0, 0, 0 }),
                TerrainMaterialPackEncoding.EncodeMaterialAlphas(new[] { 0, 0, 31, 0, 0, 0 }),
                TerrainMaterialPackEncoding.EncodeMaterialAlphas(new[] { 0, 0, 0, 31, 0, 0 })
            }
        };

        var cells = TerrainMaterialMapCodec.DecodeCells(map);
        var encoded = TerrainMaterialMapCodec.EncodeFromCells(2, 2, 2, cells);

        Assert.True(TerrainMaterialMapCodec.HasExpectedArraySizes(encoded));
        Assert.Equal(map.PackedMaterialIds[0], encoded.PackedMaterialIds[0]);
        Assert.Equal(map.PackedMaterialAlphas, encoded.PackedMaterialAlphas);
        Assert.Equal(4, encoded.GetDominantMaterialId(1, 1));
    }

    [Fact]
    public void TerrainMaterialMap_SetDominantMaterial_Updates_Block_And_Texel_Alpha()
    {
        var map = new TerrainMaterialMap
        {
            Width = 2,
            Height = 2,
            MaterialIdBlockSize = 2
        };

        map.SetDominantMaterial(1, 1, 12);

        Assert.Equal(12, map.GetDominantMaterialId(1, 1));
        Assert.Equal(TerrainMaterialPackEncoding.MaxPackedSlotValue, map.GetCell(1, 1).DominantWeight);
        Assert.Equal(map.ExpectedPackedMaterialIdCount, map.PackedMaterialIds.Length);
        Assert.Equal(map.ExpectedPackedAlphaCount, map.PackedMaterialAlphas.Length);
    }


    public void TerrainMaterialPainter_Blend_Adds_Material_And_Keeps_Exportable_Packing()
    {
        var map = new TerrainMaterialMap
        {
            Width = 4,
            Height = 4,
            MaterialIdBlockSize = 2,
            PackedMaterialIds = Enumerable.Repeat(TerrainMaterialPackEncoding.EncodeMaterialIds(new[] { 1, 2, 3, 4, 5, 6 }), 4).ToArray(),
            PackedMaterialAlphas = Enumerable.Repeat(TerrainMaterialPackEncoding.EncodeMaterialAlphas(new[] { 31, 0, 0, 0, 0, 0 }), 16).ToArray()
        };

        var result = TerrainMaterialPainter.PaintBrush(map, 1, 1, new TerrainMaterialBrushOptions
        {
            MaterialId = 8,
            Radius = 1,
            Strength = 31,
            Mode = TerrainMaterialBrushMode.Blend,
            UseFalloff = false
        });

        Assert.True(result.HasChanges);
        Assert.Equal(8, map.GetDominantMaterialId(1, 1));
        Assert.Equal(map.ExpectedPackedMaterialIdCount, map.PackedMaterialIds.Length);
        Assert.Equal(map.ExpectedPackedAlphaCount, map.PackedMaterialAlphas.Length);
        Assert.All(map.PackedMaterialIds, packed => Assert.True(TerrainMaterialPackEncoding.IsValidPackedSix5Bit(packed)));
        Assert.All(map.PackedMaterialAlphas, packed => Assert.True(TerrainMaterialPackEncoding.IsValidPackedSix5Bit(packed)));
    }

    [Fact]
    public void TerrainMaterialPainter_CellPredicate_Protects_Cells_Outside_Footprint()
    {
        var map = new TerrainMaterialMap
        {
            Width = 4,
            Height = 4,
            MaterialIdBlockSize = 2,
            PackedMaterialIds = Enumerable.Repeat(TerrainMaterialPackEncoding.EncodeMaterialIds(new[] { 2, 3, 4, 5, 6, 7 }), 4).ToArray(),
            PackedMaterialAlphas = Enumerable.Repeat(TerrainMaterialPackEncoding.EncodeMaterialAlphas(new[] { 31, 0, 0, 0, 0, 0 }), 16).ToArray()
        };

        var result = TerrainMaterialPainter.PaintBrush(map, 1, 1, new TerrainMaterialBrushOptions
        {
            MaterialId = 8,
            Radius = 2,
            Strength = 31,
            Mode = TerrainMaterialBrushMode.ReplaceDominant,
            UseFalloff = false,
            CellPredicate = (x, y) => x == 1 && y == 1
        });

        Assert.True(result.HasChanges);
        Assert.Equal(8, map.GetDominantMaterialId(1, 1));
        Assert.Equal(2, map.GetDominantMaterialId(0, 0));
        Assert.Equal(2, map.GetDominantMaterialId(2, 2));
        Assert.Equal(1, result.CellsVisited);
    }

    [Fact]
    public void Editor_ReplaceTerrainMaterialMap_Regenerates_Rd3d_Material_Blocks()
    {
        var map = new TerrainMaterialMap
        {
            Width = 2,
            Height = 2,
            MaterialIdBlockSize = 2,
            PackedMaterialIds = new[] { TerrainMaterialPackEncoding.EncodeMaterialIds(new[] { 1, 2, 3, 4, 5, 6 }) },
            PackedMaterialAlphas = new[]
            {
                TerrainMaterialPackEncoding.EncodeMaterialAlphas(new[] { 31, 0, 0, 0, 0, 0 }),
                TerrainMaterialPackEncoding.EncodeMaterialAlphas(new[] { 31, 0, 0, 0, 0, 0 }),
                TerrainMaterialPackEncoding.EncodeMaterialAlphas(new[] { 31, 0, 0, 0, 0, 0 }),
                TerrainMaterialPackEncoding.EncodeMaterialAlphas(new[] { 31, 0, 0, 0, 0, 0 })
            }
        };

        var project = new IslandProject
        {
            Rd3dFilePath = "rd3d.xml",
            Rd3d = new Rd3dDefinition
            {
                MaterialMapWidth = 2,
                MaterialMapHeight = 2,
                MaterialIDBlockSize = 2,
                MaterialMap = map.Clone()
            },
            RawContent = new RawProjectContent
            {
                Rd3dText = "<Content><MaterialMapWidth>2</MaterialMapWidth><MaterialMapHeight>2</MaterialMapHeight><MaterialIDBlockSize>2</MaterialIDBlockSize><MaterialIDs2>0</MaterialIDs2><MaterialAlphas2>0 0 0 0</MaterialAlphas2></Content>"
            }
        };

        map.SetDominantMaterialSafe(0, 0, 8);
        var state = new TerrainMaterialEditState();
        state.Add(new TerrainMaterialPaintResult { CellsChanged = 1, BlocksChanged = 1 }, new TerrainMaterialBrushOptions { MaterialId = 8 });

        new IslandProjectEditor().ReplaceTerrainMaterialMap(project, map, state);

        Assert.Contains("MaterialPainting", project.ModifiedSections);
        Assert.Contains("<MaterialIDs2>", project.RawContent.Rd3dText);
        Assert.DoesNotContain("<MaterialIDs2>0</MaterialIDs2>", project.RawContent.Rd3dText);
        Assert.True(project.MaterialEditState.HasEdits);
    }


    [Fact]
    public void TerrainMaterialPainter_StampPickedCell_Repaints_All_Captured_Slots()
    {
        var map = new TerrainMaterialMap
        {
            Width = 4,
            Height = 4,
            MaterialIdBlockSize = 2,
            PackedMaterialIds = Enumerable.Repeat(TerrainMaterialPackEncoding.EncodeMaterialIds(new[] { 1, 2, 3, 4, 5, 6 }), 4).ToArray(),
            PackedMaterialAlphas = Enumerable.Repeat(TerrainMaterialPackEncoding.EncodeMaterialAlphas(new[] { 31, 0, 0, 0, 0, 0 }), 16).ToArray()
        };

        map.SetCell(0, 0, new[] { 1, 7, 8, 9, 10, 11 }, new[] { 4, 8, 10, 6, 3, 0 });
        var pattern = TerrainMaterialCellPattern.FromCell(map.GetCell(0, 0));

        var result = TerrainMaterialPainter.PaintBrush(map, 3, 3, new TerrainMaterialBrushOptions
        {
            Radius = 1,
            Strength = 31,
            Mode = TerrainMaterialBrushMode.StampPickedCell,
            UseFalloff = false,
            CellPattern = pattern
        });

        var painted = map.GetCell(3, 3);
        Assert.True(result.HasChanges);
        Assert.Equal(new[] { 1, 7, 8, 9, 10, 11 }, painted.MaterialIds);
        Assert.Equal(TerrainMaterialPackEncoding.NormalizeAlphaWeights(new[] { 4, 8, 10, 6, 3, 0 }), painted.AlphaWeights);
        Assert.Equal(8, painted.DominantMaterialId);
        Assert.All(map.PackedMaterialIds, packed => Assert.True(TerrainMaterialPackEncoding.IsValidPackedSix5Bit(packed)));
        Assert.All(map.PackedMaterialAlphas, packed => Assert.True(TerrainMaterialPackEncoding.IsValidPackedSix5Bit(packed)));
    }

    [Fact]
    public void ProjectTextRegenerator_Rd3d_Keeps_Only_One_PropGrid()
    {
        var project = new IslandProject
        {
            Rd3d = new Rd3dDefinition
            {
                PropGrid = new PropGridDefinition
                {
                    FileNames = { "data/graphics/props/new.cfg" },
                    Instances =
                    {
                        new PropGridInstanceDefinition
                        {
                            RawNoneXml = "<None><FileNameIndex>0</FileNameIndex><Position>1 2 3</Position></None>"
                        }
                    }
                }
            },
            RawContent = new RawProjectContent
            {
                Rd3dText = """
<Content>
  <GridWidth>1</GridWidth>
  <GridHeight>1</GridHeight>
  <MaterialMapWidth>0</MaterialMapWidth>
  <MaterialMapHeight>0</MaterialMapHeight>
  <MaterialIDBlockSize>2</MaterialIDBlockSize>
  <TintMapWidth>0</TintMapWidth>
  <TintMapHeight>0</TintMapHeight>
  <NodeSize>0</NodeSize>
  <UnitScale>0.03125</UnitScale>
  <PageSize>0</PageSize>
  <TintPageSize>0</TintPageSize>
  <MinMeshLevel>0</MinMeshLevel>
  <NodesPerMeshCluster>0</NodesPerMeshCluster>
  <UsingSplitImageHeightMaps>False</UsingSplitImageHeightMaps>
  <HeightPageBorderTexels>0</HeightPageBorderTexels>
  <PropGrid><FileNames><None>old_a.cfg</None></FileNames><Instances></Instances></PropGrid>
  <PropGrid><FileNames><None>old_b.cfg</None></FileNames><Instances></Instances></PropGrid>
</Content>
"""
            }
        };

        new ProjectTextRegenerator().RegenerateRd3dText(project);

        Assert.Equal(1, Regex.Matches(project.RawContent.Rd3dText!, "<PropGrid>", RegexOptions.IgnoreCase).Count);
        Assert.Contains("data/graphics/props/new.cfg", project.RawContent.Rd3dText);
        Assert.DoesNotContain("old_b.cfg", project.RawContent.Rd3dText);
    }

    [Fact]
    public void ProjectTextRegenerator_Rd3d_Does_Not_Add_Empty_MaterialSetFileName_At_Content_End()
    {
        var project = new IslandProject
        {
            Rd3d = new Rd3dDefinition
            {
                MaterialSetFileName = null,
                PropGrid = new PropGridDefinition()
            },
            RawContent = new RawProjectContent
            {
                Rd3dText = """
<Content>
  <GridWidth>1</GridWidth>
  <GridHeight>1</GridHeight>
  <MaterialMapWidth>0</MaterialMapWidth>
  <MaterialMapHeight>0</MaterialMapHeight>
  <MaterialIDBlockSize>2</MaterialIDBlockSize>
  <TintMapWidth>0</TintMapWidth>
  <TintMapHeight>0</TintMapHeight>
  <NodeSize>0</NodeSize>
  <UnitScale>0.03125</UnitScale>
  <PageSize>0</PageSize>
  <TintPageSize>0</TintPageSize>
  <MinMeshLevel>0</MinMeshLevel>
  <NodesPerMeshCluster>0</NodesPerMeshCluster>
  <UsingSplitImageHeightMaps>False</UsingSplitImageHeightMaps>
  <HeightPageBorderTexels>0</HeightPageBorderTexels>
</Content>
"""
            }
        };

        new ProjectTextRegenerator().RegenerateRd3dText(project);

        Assert.DoesNotContain("<MaterialSetFileName>", project.RawContent.Rd3dText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"<MaterialSetFileName>\s*</MaterialSetFileName>\s*</Content>", project.RawContent.Rd3dText!);
    }

    [Fact]
    public void ProjectTextRegenerator_Rd3d_Inserts_MaterialSetFileName_Near_Terrain_Params_Not_At_Content_End()
    {
        var project = new IslandProject
        {
            Rd3d = new Rd3dDefinition
            {
                MaterialSetFileName = "data/config/materials/custom_terrain.xml",
                PropGrid = new PropGridDefinition()
            },
            RawContent = new RawProjectContent
            {
                Rd3dText = """
<Content>
  <GridWidth>1</GridWidth>
  <GridHeight>1</GridHeight>
  <MaterialMapWidth>0</MaterialMapWidth>
  <MaterialMapHeight>0</MaterialMapHeight>
  <MaterialIDBlockSize>2</MaterialIDBlockSize>
  <TintMapWidth>0</TintMapWidth>
  <TintMapHeight>0</TintMapHeight>
  <NodeSize>0</NodeSize>
  <UnitScale>0.03125</UnitScale>
  <PageSize>0</PageSize>
  <TintPageSize>0</TintPageSize>
  <MinMeshLevel>0</MinMeshLevel>
  <NodesPerMeshCluster>0</NodesPerMeshCluster>
  <UsingSplitImageHeightMaps>False</UsingSplitImageHeightMaps>
  <HeightPageBorderTexels>0</HeightPageBorderTexels>
  <MaterialIDs2>1</MaterialIDs2>
  <MaterialAlphas2>1</MaterialAlphas2>
</Content>
"""
            }
        };

        new ProjectTextRegenerator().RegenerateRd3dText(project);

        var text = project.RawContent.Rd3dText!;
        Assert.Equal(1, Regex.Matches(text, "<MaterialSetFileName>", RegexOptions.IgnoreCase).Count);
        Assert.True(text.IndexOf("<HeightPageBorderTexels>", StringComparison.OrdinalIgnoreCase)
            < text.IndexOf("<MaterialSetFileName>", StringComparison.OrdinalIgnoreCase));
        Assert.True(text.IndexOf("<MaterialSetFileName>", StringComparison.OrdinalIgnoreCase)
            < text.IndexOf("<MaterialIDs2>", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotMatch(@"<MaterialSetFileName>.*?</MaterialSetFileName>\s*</Content>", text);
    }

    [Fact]
    public void EmbeddedMaterialSets_Contain_UserLibrary_And_Create_Exact_Rd3d_Game_Path()
    {
        var library = new EmbeddedMaterialSetLibrary();
        var expectedNames = new[]
        {
            "arctic_01_test.xml",
            "colony_02_dlc10_scenario02.xml",
            "colony02_01_storyisland_03.xml",
            "colony02_01.xml",
            "south_america_01.xml",
            "terra_01.xml"
        };
        var available = library.GetAvailableFileNames();
        foreach (var expectedName in expectedNames)
        {
            Assert.Contains(expectedName, available, StringComparer.OrdinalIgnoreCase);
            Assert.True(library.TryRead(expectedName, out var embeddedText));
            Assert.False(string.IsNullOrWhiteSpace(embeddedText));
        }

        Assert.True(library.TryRead("terra_01.xml", out var text));
        var project = new IslandProject
        {
            Rd3d = new Rd3dDefinition { PropGrid = new PropGridDefinition() },
            RawContent = new RawProjectContent
            {
                Rd3dText = """
<Content>
  <GridWidth>1</GridWidth>
  <GridHeight>1</GridHeight>
  <HeightPageBorderTexels>0</HeightPageBorderTexels>
</Content>
"""
            }
        };

        new IslandProjectEditor().ReplaceMaterialSet(project, "terra_01.xml", text, "terra_01.xml", updateRd3dReference: true);

        const string expectedPath = "data/config/engine/material_sets/terra_01.xml";
        Assert.Equal(expectedPath, project.Rd3d.MaterialSetFileName);
        Assert.Contains($"<MaterialSetFileName>{expectedPath}</MaterialSetFileName>", project.RawContent.Rd3dText);
        Assert.Equal(1, Regex.Matches(project.RawContent.Rd3dText!, "<MaterialSetFileName>", RegexOptions.IgnoreCase).Count);
    }

    [Fact]
    public void MaterialSetEnvironmentSynchronizer_Changes_Natural_PropGrid_Assets_And_Preserves_Gameplay_Objects()
    {
        var project = new IslandProject
        {
            Rd3d = new Rd3dDefinition
            {
                PropGrid = new PropGridDefinition
                {
                    FileNames = new List<string>
                    {
                        "data/graphics/props/terrain_props/rocks/rocks_moderate/rock_moderate_04.prp",
                        "data/graphics/buildings/residence/residence_tier01.prp"
                    }
                }
            }
        };
        var selected = new MaterialSetDefinition { FileName = "south_america_01.xml" };
        selected.Materials.Add(new MaterialDefinition
        {
            Id = 20,
            ObjectFileName = "data/graphics/props/terrain_props/vegetation/grass/flowers_jungle_01.prp"
        });

        var report = new MaterialSetEnvironmentSynchronizer().Synchronize(project, null, selected);

        Assert.Equal("Jungle", report.TargetBiome);
        Assert.Equal(1, report.PropGridFileNamesUpdated);
        Assert.Contains("jungle", project.Rd3d.PropGrid.FileNames[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("data/graphics/buildings/residence/residence_tier01.prp", project.Rd3d.PropGrid.FileNames[1]);
    }

    [Fact]
    public void MaterialSetEnvironmentSynchronizer_Maps_GroundObjects_By_Material_Id_And_Reports_Page()
    {
        var key = new TerrainPageKey(0, 1, 2);
        var groundObject = new GroundObjectInstance
        {
            ObjectFileName = "data\\graphics\\props\\terrain_props\\vegetation\\grass\\grass_moderate_02.prp",
            PositionXZ = new Float2(12, 34),
            RotationY = 45,
            Coverage = 200
        };
        var project = new IslandProject();
        project.MeshPages[key] = new MeshPage
        {
            Key = key,
            Clusters = new List<MeshCluster> { new() { GroundObjects = new List<GroundObjectInstance> { groundObject } } }
        };
        var previous = new MaterialSetDefinition { FileName = "terra_01.xml" };
        previous.Materials.Add(new MaterialDefinition { Id = 17, ObjectFileName = groundObject.ObjectFileName });
        var selected = new MaterialSetDefinition { FileName = "south_america_01.xml" };
        selected.Materials.Add(new MaterialDefinition
        {
            Id = 17,
            ObjectFileName = "data\\graphics\\props\\terrain_props\\vegetation\\grass\\grass_jungle_01.prp"
        });

        var report = new MaterialSetEnvironmentSynchronizer().Synchronize(project, previous, selected);

        Assert.Equal(1, report.GroundObjectsUpdated);
        Assert.Contains(key, report.AffectedMeshPages);
        Assert.Equal("data\\graphics\\props\\terrain_props\\vegetation\\grass\\grass_jungle_01.prp", groundObject.ObjectFileName);
        Assert.Equal(new Float2(12, 34), groundObject.PositionXZ);
        Assert.Equal(45, groundObject.RotationY);
        Assert.Equal(200, groundObject.Coverage);
    }

    [Fact]
    public void MaterialSetEnvironmentSynchronizer_Replaces_Other_Biomes_And_Keeps_PropGrid_Exportable()
    {
        var project = new IslandProject
        {
            Rd3d = new Rd3dDefinition
            {
                PropGrid = new PropGridDefinition
                {
                    FileNames = new List<string>
                    {
                        "data/graphics/props/terrain_props/vegetation/bushes/bush_jungle_01_large.prp",
                        "data/graphics/props/terrain_props/rocks/rocks_jungle/rock_jungle_03.prp",
                        "data/graphics/buildings/harbour/trading_post.prp"
                    },
                    Instances = new List<PropGridInstanceDefinition>
                    {
                        new() { RawNoneXml = "<None><Index>0</Index><Position>1 2 3</Position></None>" },
                        new() { RawNoneXml = "<None><Index>1</Index><Position>4 5 6</Position></None>" },
                        new() { RawNoneXml = "<None><Index>2</Index><Position>7 8 9</Position></None>" }
                    }
                }
            }
        };
        var key = new TerrainPageKey(1, 0, 0);
        project.MeshPages[key] = new MeshPage
        {
            Key = key,
            Clusters = new List<MeshCluster>
            {
                new()
                {
                    GroundObjects = new List<GroundObjectInstance>
                    {
                        new() { ObjectFileName = "data\\graphics\\props\\terrain_props\\vegetation\\grass\\flowers_jungle_02.prp" }
                    }
                }
            }
        };
        var selected = new MaterialSetDefinition { FileName = "colony02_01.xml" };

        var report = new MaterialSetEnvironmentSynchronizer().Synchronize(project, null, selected);

        Assert.Equal("Colony02", report.TargetBiome);
        Assert.DoesNotContain(project.Rd3d.PropGrid.FileNames, path => path.Contains("jungle", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(project.Rd3d.PropGrid.FileNames, path => path.Contains("colony02", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(project.Rd3d.PropGrid.FileNames, path => path.Contains("trading_post", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, project.Rd3d.PropGrid.Instances.Count);
        Assert.Contains(project.Rd3d.PropGrid.Instances, instance => instance.RawNoneXml.Contains("1 2 3", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(project.Rd3d.PropGrid.Instances, instance => instance.RawNoneXml.Contains("<Index>0</Index>", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(project.Rd3d.PropGrid.Instances, instance => instance.RawNoneXml.Contains("<Index>1</Index>", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(project.MeshPages[key].Clusters[0].GroundObjects);
        Assert.True(report.PropGridFileNamesUpdated >= 2);
        Assert.Equal(0, report.PropGridInstancesRemoved);
        Assert.Equal(1, report.GroundObjectsRemoved);
    }

    [Fact]
    public void MaterialSetEnvironmentSynchronizer_Uses_Vanilla_Shared_Assets_Without_False_Foreign_Filtering()
    {
        var sharedVanillaPath = "data/graphics/props/terrain_props/vegetation/bushes/bush_moderate_01_large.prp";
        var project = new IslandProject
        {
            Rd3d = new Rd3dDefinition
            {
                PropGrid = new PropGridDefinition { FileNames = new List<string> { sharedVanillaPath } }
            }
        };
        var selected = new MaterialSetDefinition { FileName = "south_america_01.xml" };

        var report = new MaterialSetEnvironmentSynchronizer().Synchronize(project, null, selected);

        Assert.Equal(sharedVanillaPath, Assert.Single(project.Rd3d.PropGrid.FileNames));
        Assert.Equal(0, report.PropGridFileNamesUpdated);
        Assert.Contains(report.Notes, note => note.Contains("Profil vanilla Jungle", StringComparison.Ordinal));
    }

    [Fact]
    public void MaterialSetEnvironmentSynchronizer_Replaces_Generic_Asset_With_Target_Vanilla_Profile()
    {
        var project = new IslandProject
        {
            Rd3d = new Rd3dDefinition
            {
                PropGrid = new PropGridDefinition
                {
                    FileNames = new List<string>
                    {
                        "data/graphics/props/terrain_props/building_deco_plants/building_deco_tree_01.prp",
                        "data/graphics/buildings/residence/residence_tier01.prp"
                    },
                    Instances = new List<PropGridInstanceDefinition>
                    {
                        new() { RawNoneXml = "<None><Index>0</Index></None>" },
                        new() { RawNoneXml = "<None><Index>1</Index></None>" }
                    }
                }
            }
        };
        var selected = new MaterialSetDefinition { FileName = "arctic_01.xml" };

        var report = new MaterialSetEnvironmentSynchronizer().Synchronize(project, null, selected);

        Assert.Equal(2, project.Rd3d.PropGrid.FileNames.Count);
        Assert.Contains(project.Rd3d.PropGrid.FileNames, path => path.Contains("dlc03", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(project.Rd3d.PropGrid.FileNames, path => path.Contains("building_deco_tree_01", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(project.Rd3d.PropGrid.FileNames, path => path.Contains("residence_tier01", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, project.Rd3d.PropGrid.Instances.Count);
        Assert.Equal(1, report.PropGridFileNamesUpdated);
        Assert.Equal(0, report.PropGridInstancesRemoved);
    }

    [Fact]
    public void MaterialSetEnvironmentSynchronizer_Uses_AssetsXml_Variants_For_Target_Session()
    {
        var targetVariant = "data/dlc06/graphics/props/terrain_props/rocks/rocks_colony02/rock_colony02_01.prp";
        var project = new IslandProject
        {
            Rd3d = new Rd3dDefinition
            {
                PropGrid = new PropGridDefinition { FileNames = new List<string> { targetVariant } }
            }
        };
        var selected = new MaterialSetDefinition { FileName = "colony02_01.xml" };

        var report = new MaterialSetEnvironmentSynchronizer().Synchronize(project, null, selected);

        Assert.Equal(targetVariant, Assert.Single(project.Rd3d.PropGrid.FileNames));
        Assert.Equal(0, report.PropGridFileNamesUpdated);
    }

    [Fact]
    public void ReplaceMaterialSet_Prepares_Rd3d_And_Mesh_Writes_Without_Previous_Biome_References()
    {
        var project = new IslandProject
        {
            MaterialSet = new MaterialSetDefinition { FileName = "south_america_01.xml" },
            Rd3d = new Rd3dDefinition
            {
                PropGrid = new PropGridDefinition
                {
                    FileNames = new List<string>
                    {
                        "data/graphics/props/terrain_props/vegetation/bushes/bush_jungle_01_large.prp",
                        "data/graphics/props/terrain_props/rocks/rocks_jungle/rock_jungle_01.prp"
                    },
                    Instances = new List<PropGridInstanceDefinition>
                    {
                        new() { RawNoneXml = "<None><Index>1</Index><Position>1 2 3</Position></None>" }
                    }
                }
            },
            RawContent = new RawProjectContent
            {
                Rd3dText = "<Content><GridWidth>384</GridWidth><GridHeight>384</GridHeight><PropGrid><FileNames></FileNames><Instances></Instances></PropGrid></Content>"
            }
        };
        var key = new TerrainPageKey(1, 0, 0);
        project.MeshPages[key] = new MeshPage
        {
            Key = key,
            OriginalRelativePath = "_gamedata/test/meshes/1/0x0.xml",
            Clusters = new List<MeshCluster>
            {
                new()
                {
                    GroundObjects = new List<GroundObjectInstance>
                    {
                        new() { ObjectFileName = "data\\graphics\\props\\terrain_props\\vegetation\\grass\\flowers_jungle_01.prp" }
                    }
                }
            }
        };
        const string colony02MaterialSet = "<Material><ID>0</><Type>0</><FileName>data/dlc06/graphics/landscape/terrain/colony02_ground.psd</><ObjectFileName></>";

        var report = new IslandProjectEditor().ReplaceMaterialSet(
            project,
            "colony02_01.xml",
            colony02MaterialSet,
            "colony02_01.xml",
            updateRd3dReference: true);

        Assert.True(report.HasChanges);
        Assert.DoesNotContain("jungle", project.RawContent.Rd3dText!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("colony02", project.RawContent.Rd3dText!, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(project.LocalRegeneration);
        var meshWrite = Assert.Single(project.LocalRegeneration!.GeneratedMeshPageWrites);
        Assert.DoesNotContain("jungle", meshWrite.XmlText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("flowers_jungle", meshWrite.XmlText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GroundObjects uniquement", meshWrite.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(project.ValidationReport.Messages, message =>
            message.Severity == ValidationSeverity.Error &&
            message.Message.Contains("LOD0/LOD1 non supportées", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReplaceMaterialSet_Remaps_MaterialMap_And_Session_Without_Stale_Terra_Ids()
    {
        var map = new TerrainMaterialMap
        {
            Width = 2,
            Height = 2,
            MaterialIdBlockSize = 2,
            PackedMaterialIds = new[] { TerrainMaterialPackEncoding.EncodeMaterialIds(new[] { 30, 31, 29, 28, 27, 26 }) },
            PackedMaterialAlphas = Enumerable.Repeat(
                TerrainMaterialPackEncoding.EncodeAlphaWeights(new[] { 31, 0, 0, 0, 0, 0 }), 4).ToArray()
        };
        var project = new IslandProject
        {
            MaterialSet = new MaterialSetDefinition
            {
                // Reproduit une île déjà déclarée colony02 mais dont la carte conserve
                // encore des identifiants terra_01 (cas réel de moderate_l_10).
                FileName = "colony02_01.xml",
                Materials = new List<MaterialDefinition>
                {
                    new() { Id = 6, Type = "0", FileName = "data/dlc06/graphics/landscape/terrain/colony02/colony02_mountain_01.xml" },
                    new() { Id = 0, Type = "0", FileName = "data/dlc06/graphics/landscape/terrain/colony02/colony02_beach_06.xml" }
                }
            },
            Rd3d = new Rd3dDefinition { MaterialMap = map, MaterialMapWidth = 2, MaterialMapHeight = 2 },
            RawContent = new RawProjectContent
            {
                Rd3dText = "<Content><MaterialMapWidth>2</MaterialMapWidth><MaterialMapHeight>2</MaterialMapHeight><MaterialIDBlockSize>2</MaterialIDBlockSize><MaterialIDs2>0</MaterialIDs2><MaterialAlphas2>0 0 0 0</MaterialAlphas2><PropGrid><FileNames/><Instances/></PropGrid></Content>",
                GameDataText = "<Content><GameSessionManager><SessionSettings><GlobalAmbientName>Moderate_01</GlobalAmbientName><PlayableArea>0 0 2 2</PlayableArea><VegetationPropSetName>Moderate</VegetationPropSetName></SessionSettings><EditorObject><objects/></EditorObject></GameSessionManager></Content>"
            }
        };
        const string colony02 = "<MaterialSet><Material><ID>6</><Type>0</><FileName>data/dlc06/graphics/landscape/terrain/colony02/colony02_mountain_01.xml</><ObjectFileName></></><Material><ID>0</><Type>0</><FileName>data/dlc06/graphics/landscape/terrain/colony02/colony02_beach_06.xml</><ObjectFileName></></></>";

        new IslandProjectEditor().ReplaceMaterialSet(project, "colony02_01.xml", colony02, "colony02_01.xml", true);

        var ids = project.Rd3d.MaterialMap.GetMaterialIdBlock(0, 0);
        Assert.All(ids, id => Assert.Contains(id, new[] { 0, 6 }));
        Assert.Equal(6, project.Rd3d.MaterialMap.GetDominantMaterialId(0, 0));
        Assert.Contains("<GlobalAmbientName>Colony_02</GlobalAmbientName>", project.RawContent.GameDataText);
        Assert.Contains("<VegetationPropSetName>Colony02</VegetationPropSetName>", project.RawContent.GameDataText);
    }

    [Fact]
    public void UsedMaterialBits_Preserve_Local_Masks_And_Add_Nearby_Painted_Materials()
    {
        var map = new TerrainMaterialMap
        {
            Width = 2,
            Height = 1,
            MaterialIdBlockSize = 1,
            PackedMaterialIds = new[]
            {
                TerrainMaterialPackEncoding.EncodeMaterialIds(new[] { 3, 0, 0, 0, 0, 0 }),
                TerrainMaterialPackEncoding.EncodeMaterialIds(new[] { 9, 0, 0, 0, 0, 0 })
            },
            PackedMaterialAlphas = new[]
            {
                TerrainMaterialPackEncoding.EncodeAlphaWeights(new[] { 31, 0, 0, 0, 0, 0 }),
                TerrainMaterialPackEncoding.EncodeAlphaWeights(new[] { 31, 0, 0, 0, 0, 0 })
            }
        };
        var key = new TerrainPageKey(2, 0, 0);
        var page = new MeshPage
        {
            Key = key,
            OriginalRelativePath = "_gamedata/test/meshes/2/0x0.xml",
            DeclaredWidth = 2,
            DeclaredHeight = 1,
            Clusters =
            {
                new MeshCluster { UsedMaterialBits = 1 << 3, Vertices = { new MeshVertex { Raw0 = 0, Raw2 = 0 } } },
                new MeshCluster { UsedMaterialBits = 1 << 9, Vertices = { new MeshVertex { Raw0 = 16, Raw2 = 0 } } }
            }
        };
        var project = new IslandProject
        {
            Rd3d = new Rd3dDefinition { GridWidth = 32, GridHeight = 16, MaterialMap = map },
            MaterialSet = new MaterialSetDefinition
            {
                Materials = new List<MaterialDefinition>
                {
                    new() { Id = 0 },
                    new() { Id = 3 },
                    new() { Id = 9 }
                }
            },
            MeshPages = { [key] = page }
        };

        var changedPages = new IslandProjectEditor().ResynchronizeMeshUsedMaterialBitsFromMaterialMap(project);

        Assert.Equal(1, changedPages);
        Assert.Equal((1 << 3) | (1 << 9), page.Clusters[0].UsedMaterialBits);
        Assert.Equal((1 << 3) | (1 << 9), page.Clusters[1].UsedMaterialBits);
        Assert.Single(project.LocalRegeneration!.GeneratedMeshPageWrites);
    }



    public void CoastTransitionPainter_Classifies_HeightMap_Into_Water_Beach_And_Land()
    {
        var map = new TerrainMaterialMap
        {
            Width = 4,
            Height = 1,
            MaterialIdBlockSize = 2,
            PackedMaterialIds = new uint[2],
            PackedMaterialAlphas = new uint[4]
        };
        var heightMap = new CoarseHeightMap
        {
            Width = 5,
            Height = 2,
            SampleEncoding = TerrainHeightSampleEncoding.WorldUnits,
            Samples = new float[]
            {
                -4f, -1f, 0.2f, 1.0f, 3.0f,
                -4f, -1f, 0.6f, 3f, 7f
            }
        };
        var options = new TerrainCoastTransitionOptions
        {
            DeepWaterMaterialId = 3,
            ShallowWaterMaterialId = 4,
            BeachMaterialId = 1,
            LowLandMaterialId = 2,
            DeepWaterMaxHeight = -2f,
            WaterLineHeight = -0.2f,
            BeachMaxHeight = 1.5f,
            LowLandMaxHeight = 4f
        };

        var result = TerrainCoastTransitionPainter.ApplyHeightToMaterialTransition(map, heightMap, options, out var stats);

        Assert.True(result.HasChanges);
        Assert.True(stats.DeepWaterCells > 0);
        Assert.True(stats.ShallowWaterCells > 0);
        Assert.True(stats.BeachCells > 0);
        Assert.True(stats.LowLandCells > 0);
        Assert.Equal(3, map.GetDominantMaterialId(0, 0));
        Assert.Equal(4, map.GetDominantMaterialId(1, 0));
        Assert.Equal(1, map.GetDominantMaterialId(2, 0));
        Assert.Equal(2, map.GetDominantMaterialId(3, 0));
    }

    [Fact]
    public void TopologyMaterialPainter_Classifies_Local_Form_And_Ignores_Absolute_Height()
    {
        static TerrainMaterialMap CreateMaterialMap()
        {
            var map = new TerrainMaterialMap
            {
                Width = 5,
                Height = 5,
                MaterialIdBlockSize = 1,
                PackedMaterialIds = new uint[25],
                PackedMaterialAlphas = new uint[25]
            };
            for (var y = 0; y < 5; y++)
            {
                for (var x = 0; x < 5; x++)
                {
                    map.SetDominantMaterialSafe(x, y, 2, TerrainMaterialPackEncoding.MaxPackedSlotValue);
                }
            }
            map.SetDominantMaterialSafe(0, 0, 9, TerrainMaterialPackEncoding.MaxPackedSlotValue);
            return map;
        }

        static CoarseHeightMap CreateHeightMap(float offset)
        {
            var samples = Enumerable.Repeat(offset, 25).ToArray();
            samples[(2 * 5) + 2] = offset + 4f;
            samples[(3 * 5) + 3] = offset - 2f;
            return new CoarseHeightMap
            {
                Width = 5,
                Height = 5,
                SampleEncoding = TerrainHeightSampleEncoding.WorldUnits,
                Samples = samples
            };
        }

        var options = new TerrainTopologyMaterialOptions
        {
            FlatMaterialId = 10,
            GentleSlopeMaterialId = 11,
            SteepSlopeMaterialId = 12,
            RidgeMaterialId = 13,
            HollowMaterialId = 14,
            FlatSlopeMaximum = 0.1f,
            SteepSlopeMinimum = 0.8f,
            CurvatureSensitivity = 0.25f,
            AnalysisRadius = 1,
            PreserveExistingWaterAndBeach = true
        };
        options.PreservedMaterialIds.Add(9);

        var lowMap = CreateMaterialMap();
        var highMap = CreateMaterialMap();
        var lowResult = TerrainTopologyMaterialPainter.Apply(lowMap, CreateHeightMap(0f), options, out var lowStats);
        var highResult = TerrainTopologyMaterialPainter.Apply(highMap, CreateHeightMap(100f), options, out var highStats);

        Assert.True(lowResult.HasChanges);
        Assert.Equal(lowStats.FlatCells, highStats.FlatCells);
        Assert.Equal(lowStats.GentleSlopeCells, highStats.GentleSlopeCells);
        Assert.Equal(lowStats.SteepSlopeCells, highStats.SteepSlopeCells);
        Assert.Equal(lowStats.RidgeCells, highStats.RidgeCells);
        Assert.Equal(lowStats.HollowCells, highStats.HollowCells);
        Assert.True(lowStats.RidgeCells > 0);
        Assert.True(lowStats.HollowCells > 0);
        Assert.True(lowStats.FlatCells > 0);
        Assert.Equal(9, lowMap.GetDominantMaterialId(0, 0));
        Assert.Equal(lowMap.GetDominantMaterialId(2, 2), highMap.GetDominantMaterialId(2, 2));
    }

    [Fact]
    public void Editor_Replaces_GroundObjects_And_Generates_Mesh_Page_Write()
    {
        var key = new TerrainPageKey(0, 0, 0);
        var page = new MeshPage
        {
            Key = key,
            OriginalRelativePath = "_gamedata/test/meshes/0/0x0.xml",
            DeclaredWidth = 1,
            DeclaredHeight = 1,
            Clusters =
            {
                new MeshCluster
                {
                    UsedMaterialBits = 0,
                    GroundObjects =
                    {
                        new GroundObjectInstance
                        {
                            Id = 1,
                            ObjectFileName = "old.cfg",
                            Kind = GroundObjectKind.Decoration,
                            PositionXZ = new Float2(1, 2),
                            RotationY = 0,
                            Coverage = 1
                        }
                    }
                }
            },
            RawUnknownFields =
            {
                ["__raw"] = "<Content><Meshes><width>1</width><height>1</height><map><None><Indices></Indices><Vertices></Vertices><UsedMaterialBits>0</UsedMaterialBits><GroundObjects><None><ID>1</ID><FileName>old.cfg</FileName><PositionsXZ>1 2</PositionsXZ><RotationsY>0</RotationsY><Coverages>1</Coverages></None></GroundObjects></None></map></Meshes></Content>"
            }
        };

        var project = new IslandProject
        {
            Metadata = new IslandMetadata { MapSize = 64 },
            MeshPages = { [key] = page }
        };

        new IslandProjectEditor().ReplaceGroundObjects(project, new[]
        {
            new GroundObjectPlacement
            {
                PageKey = key,
                ClusterIndex = 0,
                Object = new GroundObjectInstance
                {
                    Id = 7,
                    ObjectFileName = "data/graphics/props/vegetation/tree.cfg",
                    Kind = GroundObjectKind.Vegetation,
                    PositionXZ = new Float2(3, 4),
                    RotationY = 1.5f,
                    Coverage = 0.75f
                }
            }
        });

        Assert.True(project.GroundObjectEditState.HasEdits);
        Assert.Single(project.LocalRegeneration!.GeneratedMeshPageWrites);
        Assert.Contains("tree.cfg", project.LocalRegeneration.GeneratedMeshPageWrites[0].XmlText);
        Assert.DoesNotContain("old.cfg", project.LocalRegeneration.GeneratedMeshPageWrites[0].XmlText);
        Assert.Contains("GroundObjects", project.ModifiedSections);
    }


    [Fact]
    public void TerrainVertexAttributeCodec_Decodes_Shader_Vertex_Layout()
    {
        var vertex = new MeshVertex
        {
            RawValues = new[] { 610, 31856, 327, -15169, -386, 8046, 28288, -29183 }
        };

        var attributes = TerrainVertexAttributeCodec.Decode(vertex);

        Assert.True(Math.Abs(attributes.TexCoord.X - 0.76855f) < 0.001f);
        Assert.True(Math.Abs(attributes.TexCoord.Y - 0.55517f) < 0.001f);
        Assert.True(Math.Abs(attributes.Normal.X - (-0.137f)) < 0.02f);
        Assert.True(attributes.Normal.Y > 0.98f);
        Assert.True(Math.Abs(attributes.Tangent.X - (-0.992f)) < 0.02f);
        Assert.False(attributes.IsSkirtVertex);
    }

    [Fact]
    public void TerrainVertexAttributeCodec_Rebuilds_Normal_Tangent_And_Preserves_Uv_Bytes()
    {
        var raw = new[] { 16, MeshVertex.EncodeWorldHeight(0f), 16, -15169, -386, 8046, 28288, -29183 };
        var originalU = raw[3];
        var originalVLow = (TerrainVertexAttributeCodec.ToUInt16Bits(raw[5]) >> 8) & 0xFF;
        var originalVHigh = (TerrainVertexAttributeCodec.ToUInt16Bits(raw[7]) >> 8) & 0xFF;

        TerrainVertexAttributeCodec.RebuildSurfaceAttributes(
            raw,
            (x, z) => 0f,
            16,
            16,
            TerrainVertexAttributeCodec.DefaultRawUnitScale);

        var rebuilt = TerrainVertexAttributeCodec.Decode(raw);
        Assert.Equal(originalU, raw[3]);
        Assert.Equal(originalVLow, (TerrainVertexAttributeCodec.ToUInt16Bits(raw[5]) >> 8) & 0xFF);
        Assert.Equal(originalVHigh, (TerrainVertexAttributeCodec.ToUInt16Bits(raw[7]) >> 8) & 0xFF);
        Assert.True(rebuilt.Normal.Y > 0.99f);
        Assert.True(Math.Abs(rebuilt.Normal.X) < 0.02f);
        Assert.True(Math.Abs(rebuilt.Normal.Z) < 0.02f);
    }

    [Fact]
    public void ConservativeMeshRegeneration_V464_Changes_Height_Without_Rebuilding_Shader_Attributes()
    {
        var key = new TerrainPageKey(2, 0, 0);
        var sourceVertices = new[]
        {
            new MeshVertex { RawValues = new[] { 0, MeshVertex.EncodeWorldHeight(1f), 0, -15169, -386, 8046, 28288, -29183 } },
            new MeshVertex { RawValues = new[] { 100, MeshVertex.EncodeWorldHeight(2f), 0, 1234, 2345, 3456, 4567, 5678 } },
            new MeshVertex { RawValues = new[] { 0, MeshVertex.EncodeWorldHeight(3f), 100, -1234, -2345, -3456, -4567, -5678 } }
        };
        var page = new MeshPage
        {
            Key = key,
            OriginalRelativePath = "meshes/2/0x0.xml",
            HasEditablePayload = true,
            HasDecodedPayload = true,
            ContainsStructuredMeshPayload = true,
            VertexStride = 8,
            ProbableHeightComponentIndex = 1,
            Clusters =
            {
                new MeshCluster
                {
                    Vertices = sourceVertices.ToList(),
                    Indices = { 0, 1, 2 }
                }
            }
        };
        var project = new IslandProject
        {
            Rd3d = new Rd3dDefinition { GridWidth = 100, GridHeight = 100, UnitScale = 1f },
            MeshPages = { [key] = page }
        };
        var map = new CoarseHeightMap
        {
            Width = 2,
            Height = 2,
            SampleEncoding = TerrainHeightSampleEncoding.WorldUnits,
            Samples = new[] { 5f, 5f, 5f, 5f }
        };

        var method = typeof(LocalTerrainDerivativeRegenerator).GetMethod(
            "BuildConservativeMeshPageWrite",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var write = Assert.IsType<RegeneratedMeshPageWrite>(method!.Invoke(
            null,
            new object?[] { project, page, map, new RectI(0, 0, 1, 1), new RectI(0, 0, 1, 1), new MeshRegenerationOptions { RebuildSurfaceAttributes = false }, null }));
        var regenerated = new MeshPageReader().Read("generated.xml", key, write.XmlText, retainRawText: false);
        var outputVertices = Assert.Single(regenerated.Clusters).Vertices;

        Assert.Equal(sourceVertices.Length, outputVertices.Count);
        for (var index = 0; index < sourceVertices.Length; index++)
        {
            Assert.Equal(MeshVertex.EncodeWorldHeight(5f), outputVertices[index].Raw1);
            Assert.Equal(sourceVertices[index].RawValues.Skip(3).Take(5), outputVertices[index].RawValues.Skip(3).Take(5));
        }
    }

    [Fact]
    public void ConservativeMeshRegeneration_V4664_Preserves_Vertices_Outside_Local_River_Bounds()
    {
        var key = new TerrainPageKey(5, 0, 0);
        var insideRaw1 = MeshVertex.EncodeWorldHeight(1f);
        var mountainRaw1 = MeshVertex.EncodeWorldHeight(37f);
        var page = new MeshPage
        {
            Key = key,
            OriginalRelativePath = "meshes/5/0x0.xml",
            HasEditablePayload = true,
            HasDecodedPayload = true,
            ContainsStructuredMeshPayload = true,
            VertexStride = 8,
            ProbableHeightComponentIndex = 1,
            Clusters =
            {
                new MeshCluster
                {
                    UsedMaterialBits = 1 << 9,
                    Vertices =
                    {
                        new MeshVertex { RawValues = new[] { 0, insideRaw1, 0, 1, 2, 3, 4, 5 } },
                        new MeshVertex { RawValues = new[] { 100, mountainRaw1, 100, 6, 7, 8, 9, 10 } },
                        new MeshVertex { RawValues = new[] { 90, mountainRaw1, 100, 11, 12, 13, 14, 15 } }
                    },
                    Indices = { 0, 1, 2 }
                }
            }
        };
        var project = new IslandProject
        {
            Rd3d = new Rd3dDefinition { GridWidth = 100, GridHeight = 100, UnitScale = 1f },
            MeshPages = { [key] = page }
        };
        var map = new CoarseHeightMap
        {
            Width = 5,
            Height = 5,
            SampleEncoding = TerrainHeightSampleEncoding.WorldUnits,
            Samples = Enumerable.Repeat(5f, 25).ToArray()
        };

        var method = typeof(LocalTerrainDerivativeRegenerator).GetMethod(
            "BuildConservativeMeshPageWrite",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var write = Assert.IsType<RegeneratedMeshPageWrite>(method!.Invoke(
            null,
            new object?[]
            {
                project,
                page,
                map,
                new RectI(0, 0, 4, 4),
                new RectI(0, 0, 0, 0),
                new MeshRegenerationOptions { RebuildSurfaceAttributes = false },
                null
            }));
        var regenerated = new MeshPageReader().Read("generated.xml", key, write.XmlText, retainRawText: false);
        var cluster = Assert.Single(regenerated.Clusters);

        Assert.Equal(MeshVertex.EncodeWorldHeight(5f), cluster.Vertices[0].Raw1);
        Assert.Equal(mountainRaw1, cluster.Vertices[1].Raw1);
        Assert.Equal(mountainRaw1, cluster.Vertices[2].Raw1);
        Assert.Equal(1 << 9, cluster.UsedMaterialBits);
        Assert.Equal(new[] { 0, 1, 2 }, cluster.Indices);
    }

    [Fact]
    public void ExpandedMeshRegeneration_V4616_Removes_Triangle_That_Crosses_Footprint_Boundary()
    {
        var cluster = new MeshCluster
        {
            Vertices =
            {
                new MeshVertex { RawValues = new[] { 0, 0, 0, 0, 0, 0, 0, 0 } },
                new MeshVertex { RawValues = new[] { 100, 0, 0, 0, 0, 0, 0, 0 } },
                new MeshVertex { RawValues = new[] { 0, 0, 100, 0, 0, 0, 0, 0 } }
            },
            Indices = { 0, 1, 2 }
        };
        var method = typeof(LocalTerrainDerivativeRegenerator).GetMethod(
            "FilterClusterToActiveFootprint",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        // Le centroïde (33,33) est actif, mais le triangle dépasse largement vers
        // l'eau. L'ancien contrôle du seul centroïde le conservait entièrement.
        method!.Invoke(null, new object[] { cluster, (Func<double, double, bool>)((x, z) => x < 50d && z < 50d) });

        Assert.Empty(cluster.Vertices);
        Assert.Empty(cluster.Indices);
    }


    [Fact]
    public void TerrainMaterialPackEncoding_V30_Does_Not_Normalize_Alpha_Sums()
    {
        var blended = TerrainMaterialPackEncoding.BlendAlphaSlot(new[] { 20, 15, 0, 0, 0, 0 }, 2, 10);

        Assert.Equal(new[] { 20, 15, 10, 0, 0, 0 }, blended);
        Assert.True(blended.Sum() > TerrainMaterialPackEncoding.MaxPackedSlotValue);

        var clamped = TerrainMaterialPackEncoding.NormalizeAlphaWeights(new[] { 40, 8, 10, 6, 3, 0 });
        Assert.Equal(new[] { 31, 8, 10, 6, 3, 0 }, clamped);
    }

    [Fact]
    public void TerrainMaterialMapValidator_V30_Unknown_MaterialIds_Are_Warnings_Not_Errors()
    {
        var project = new IslandProject
        {
            Rd3dFilePath = "rd3d.xml",
            Rd3d = new Rd3dDefinition
            {
                MaterialMapWidth = 2,
                MaterialMapHeight = 2,
                MaterialMap = new TerrainMaterialMap
                {
                    Width = 2,
                    Height = 2,
                    MaterialIdBlockSize = 2,
                    PackedMaterialIds = new[] { TerrainMaterialPackEncoding.EncodeMaterialIds(new[] { 1, 27, 0, 0, 0, 0 }) },
                    PackedMaterialAlphas = Enumerable.Repeat(TerrainMaterialPackEncoding.EncodeMaterialAlphas(new[] { 31, 8, 0, 0, 0, 0 }), 4).ToArray()
                }
            },
            MaterialSet = new MaterialSetDefinition
            {
                FileName = "terra_01.xml",
                Materials = { new MaterialDefinition { Id = 1, Type = "0", FileName = "known.xml" } }
            }
        };

        var report = new TerrainMaterialMapValidator().Validate(project);

        Assert.False(report.HasErrors);
        Assert.Contains(report.Messages, m => m.Severity == ValidationSeverity.Warning && m.Code == ValidationCode.MaterialMapEncoding && m.Message.Contains("27"));
        Assert.Contains(report.Messages, m => m.Code == ValidationCode.MaterialMapEncoding && m.Message.Contains("ne normalise pas destructivement"));
    }

    [Fact]
    public void TerrainConsistencyValidator_V30_Blocks_Unsupported_Protected_Lod_Writes()
    {
        var project = new IslandProject
        {
            LocalRegeneration = new TerrainLocalRegenerationState
            {
                GeneratedMeshPageWrites =
                {
                    new RegeneratedMeshPageWrite
                    {
                        Key = new TerrainPageKey(1, 0, 0),
                        TargetPath = "_gamedata/test/meshes/1/0x0.xml",
                        XmlText = "<Content />",
                        Summary = "réécriture mesh terrain"
                    }
                }
            }
        };

        var report = new TerrainConsistencyValidator().Validate(project);

        Assert.Contains(report.Messages, m => m.Severity == ValidationSeverity.Error && m.Message.Contains("LOD0/LOD1 non supportées"));
    }

    [Fact]
    public void TerrainBrushSpatialIndex_QueryRectangle_Uses_Box_Not_Circle()
    {
        var page = new MeshPage
        {
            Key = new TerrainPageKey(2, 0, 0),
            HasEditablePayload = true,
            Clusters =
            {
                new MeshCluster
                {
                    Vertices =
                    {
                        new MeshVertex { Raw0 = 0, Raw1 = MeshVertex.EncodeDecodedHeight(10), Raw2 = 0 },
                        new MeshVertex { Raw0 = 9, Raw1 = MeshVertex.EncodeDecodedHeight(20), Raw2 = 1 },
                        new MeshVertex { Raw0 = 11, Raw1 = MeshVertex.EncodeDecodedHeight(30), Raw2 = 1 },
                        new MeshVertex { Raw0 = 0, Raw1 = MeshVertex.EncodeDecodedHeight(40), Raw2 = 6 },
                    }
                }
            }
        };

        var index = TerrainBrushSpatialIndex.Build(new[] { page }, 2, 4);
        var hits = index.QueryRectangle(0, 0, halfWidth: 10, halfLength: 2, influenceRadius: 10);

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, hit => hit.Vertex.X == 0 && hit.Vertex.Z == 0);
        Assert.Contains(hits, hit => hit.Vertex.X == 9 && hit.Vertex.Z == 1);
        Assert.DoesNotContain(hits, hit => hit.Vertex.X == 11);
        Assert.DoesNotContain(hits, hit => hit.Vertex.Z == 6);
    }

    [Fact]
    public void TerrainBrushSpatialIndex_RectangleAverage_Returns_Weighted_Height()
    {
        var page = new MeshPage
        {
            Key = new TerrainPageKey(2, 0, 0),
            HasEditablePayload = true,
            Clusters =
            {
                new MeshCluster
                {
                    Vertices =
                    {
                        new MeshVertex { Raw0 = 0, Raw1 = MeshVertex.EncodeDecodedHeight(10), Raw2 = 0 },
                        new MeshVertex { Raw0 = 5, Raw1 = MeshVertex.EncodeDecodedHeight(20), Raw2 = 0 },
                    }
                }
            }
        };

        var index = TerrainBrushSpatialIndex.Build(new[] { page }, 2, 4);
        var average = index.ComputeWeightedDecodedAverageRectangle(0, 0, halfWidth: 10, halfLength: 2, fallback: -1);

        Assert.True(average > 10);
        Assert.True(average < 20);
    }


}
