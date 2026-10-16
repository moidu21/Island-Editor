using System.Numerics;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.Formats.Readers;
using Xunit;

namespace Anno1800IslandTool.Tests;

public class CoastAndSurfaceTests
{
    [Fact]
    public void Quantized_Surface_Duplicate_Must_Not_Delete_A_Mountain_Triangle()
    {
        // Native 2040_test_2_5: this triangle was discarded because another
        // clipped triangle has a vertex nine raw height steps above vertex 1.
        var project = FlatProject();
        var c = project.MeshPages[new(2,0,0)].Clusters[0];
        c.Vertices = new()
        {
            new() { Raw0=16192, Raw1=63645, Raw2=21611 },
            new() { Raw0=16256, Raw1=63359, Raw2=21611 },
            new() { Raw0=16192, Raw1=61322, Raw2=21548 },
            new() { Raw0=16256, Raw1=63368, Raw2=21611 },
            new() { Raw0=16192, Raw1=63604, Raw2=21611 }
        };
        c.Indices = new() { 0,1,2, 0,4,1 };
        var sampler = MeshBackedHeightSampler.Create(project,2)!;
        Assert.True(sampler.TrySample(16224,21600,out var height));
        Assert.Equal(118.4703f,height,3);
    }

    private static IslandProject FlatProject()
    {
        var vertices = new List<MeshVertex>
        {
            new() { Raw0 = 0, Raw2 = 0, Raw1 = MeshVertex.EncodeWorldHeight(.5f) },
            new() { Raw0 = 2048, Raw2 = 0, Raw1 = MeshVertex.EncodeWorldHeight(.5f) },
            new() { Raw0 = 0, Raw2 = 2048, Raw1 = MeshVertex.EncodeWorldHeight(.5f) },
            new() { Raw0 = 2048, Raw2 = 2048, Raw1 = MeshVertex.EncodeWorldHeight(.5f) }
        };
        var project = new IslandProject();
        project.Rd3d.GridWidth = project.Rd3d.GridHeight = 2048;
        project.MeshPages[new(2, 0, 0)] = new MeshPage { Key = new(2, 0, 0), HasEditablePayload = true,
            HasDecodedPayload = true, ContainsStructuredMeshPayload = true, DeclaredWidth = 1, DeclaredHeight = 1,
            OriginalRelativePath = "meshes/2/0x0.xml", Clusters = new() { new() { Vertices = vertices, Indices = new() { 0, 2, 1, 1, 2, 3 } } } };
        return project;
    }

    [Fact]
    public void Coastal_Rebuild_Roundtrips_Without_False_Skirts_At_Near_And_Far_Lod()
    {
        var project = FlatProject();
        project.Root.MapSize = 64;
        project.Root.ActiveMapGrid = new Int2(8,8);
        project.Root.ActiveMapGridBits = ActiveMapGridMaskCodec.Encode(new Int2(8,8), Enumerable.Repeat(true,64).ToArray());
        var far = FlatProject().MeshPages[new(2,0,0)];
        far.Key = new(8,0,0);
        far.OriginalRelativePath = "meshes/8/0x0.xml";
        project.MeshPages[far.Key] = far;
        var samples = new float[65 * 65];
        for (var y = 0; y < 65; y++)
        for (var x = 0; x < 65; x++) samples[y * 65 + x] = TerrainHeightEncoding.WorldHeightToGameDataRaw(x <= 32 ? .5f : -8f);
        var map = new CoarseHeightMap { Width = 65, Height = 65, Samples = samples, SampleEncoding = TerrainHeightSampleEncoding.GameDataRawUInt16 };
        var generator = new LocalTerrainDerivativeRegenerator();
        var state = generator.RegenerateAroundBounds(project, map, "Mesh", new(2,0,0), new(0,0,64,64), new(0,0,64,64));
        state.UseCoarseHeightMap = true;
        var writes = generator.GenerateExpandedActiveMeshPageWrites(project, map, state, new MeshRegenerationOptions { RebuildSurfaceAttributes = true });
        Assert.Equal(2, writes.Count);
        foreach (var write in writes)
        {
            var page = new MeshPageReader().Read(write.TargetPath, write.Key, write.XmlText, false);
            Assert.All(page.Clusters.SelectMany(c => c.Vertices), v => Assert.False(TerrainVertexAttributeCodec.Decode(v).IsSkirtVertex));
            Assert.All(page.Clusters, c => Assert.All(c.Indices, i => Assert.InRange(i, 0, c.Vertices.Count - 1)));
            project.MeshPages[page.Key] = page;
        }
        foreach (var lod in new[] { 2,8 })
        {
            var sampler = MeshBackedHeightSampler.Create(project, lod)!;
            Assert.True(sampler.TrySample(1024, 1000, out var dry));
            Assert.Equal(.5f, dry, 4);
            Assert.True(sampler.TrySample(1056, 1000, out var wet));
            Assert.Equal(-8f, wet, 4);
        }
    }

    [Fact]
    public void SurfaceSampler_Interpolates_Center_Far_From_All_Vertices()
    {
        var sampler = MeshBackedHeightSampler.Create(FlatProject(), 2)!;
        Assert.True(sampler.TrySample(1000, 1000, out var height));
        Assert.Equal(.5f, height, 5);
        Assert.False(sampler.TrySample(4000, 4000, out _));
    }

    [Fact]
    public void CoarseSync_Removes_Old_Mountain_Inside_Large_Flat_Triangles()
    {
        var project = FlatProject();
        var map = new CoarseHeightMap { Width = 33, Height = 33, SampleEncoding = TerrainHeightSampleEncoding.GameDataRawUInt16,
            Samples = Enumerable.Repeat((float)TerrainHeightEncoding.WorldHeightToGameDataRaw(25), 33 * 33).ToArray() };
        var result = new LocalTerrainDerivativeRegenerator().BuildMeshBackedCoarseHeightMap(project, map, 2);
        Assert.All(result.Samples, raw => Assert.Equal(.5f, TerrainHeightEncoding.GameDataRawToWorldHeight((int)raw)));
    }

    [Fact]
    public void HigherLod_Uses_The_Flat_Surface_Not_The_Old_Heightmap()
    {
        var project = FlatProject();
        project.MeshPages[new(3, 0, 0)] = new MeshPage { Key = new(3, 0, 0), OriginalRelativePath = "meshes/3/0x0.xml",
            HasEditablePayload = true, HasDecodedPayload = true, ContainsStructuredMeshPayload = true,
            DeclaredWidth = 1, DeclaredHeight = 1, Clusters = new() { new() { Vertices = new()
            {
                new() { Raw0 = 900, Raw2 = 900, Raw1 = MeshVertex.EncodeWorldHeight(20) },
                new() { Raw0 = 1100, Raw2 = 900, Raw1 = MeshVertex.EncodeWorldHeight(20) },
                new() { Raw0 = 900, Raw2 = 1100, Raw1 = MeshVertex.EncodeWorldHeight(20) }
            }, Indices = new() { 0, 1, 2 } } } };
        var map = new CoarseHeightMap { Width = 33, Height = 33, SampleEncoding = TerrainHeightSampleEncoding.GameDataRawUInt16,
            Samples = Enumerable.Repeat((float)TerrainHeightEncoding.WorldHeightToGameDataRaw(25), 33 * 33).ToArray() };
        var regenerator = new LocalTerrainDerivativeRegenerator();
        var state = regenerator.RegenerateAroundBounds(project, map, "Mesh", new(2, 0, 0), new(0, 0, 32, 32), new(0, 0, 32, 32));
        var writes = regenerator.GenerateMeshPageWrites(project, map, state, true, 99);
        var far = Assert.Single(writes.Where(w => w.Key.Lod == 3));
        var page = new MeshPageReader().Read(far.TargetPath, far.Key, far.XmlText, false);
        Assert.All(page.Clusters.SelectMany(c => c.Vertices), v => Assert.Equal(.5f, v.WorldHeight, 5));
    }

    [Fact]
    public void Conservative_Rebuild_Restores_A_Flattened_Vanilla_Skirt()
    {
        var project = new IslandProject();
        project.Rd3d.GridWidth = project.Rd3d.GridHeight = 2048;
        var flattenedHeight = MeshVertex.EncodeWorldHeight(3f);
        var cluster = new MeshCluster
        {
            Vertices = new()
            {
                new() { Raw0 = 0, Raw2 = 0, Raw1 = flattenedHeight },
                new() { Raw0 = 256, Raw2 = 0, Raw1 = flattenedHeight },
                new() { Raw0 = 0, Raw2 = 256, Raw1 = flattenedHeight },
                // Même X/Z que le sommet 0 : ancienne exportation ayant aplati la jupe.
                new() { Raw0 = 0, Raw2 = 0, Raw1 = flattenedHeight }
            },
            // Le sommet 0 appartient à la surface et à la paroi ; le doublon 3
            // appartient uniquement à la paroi verticale.
            Indices = new() { 0, 2, 1, 3, 0, 1 }
        };
        project.MeshPages[new(2, 0, 0)] = new MeshPage
        {
            Key = new(2, 0, 0), OriginalRelativePath = "meshes/2/0x0.xml",
            HasEditablePayload = true, HasDecodedPayload = true, ContainsStructuredMeshPayload = true,
            DeclaredWidth = 1, DeclaredHeight = 1, Clusters = new() { cluster }
        };
        var map = new CoarseHeightMap
        {
            Width = 33, Height = 33, SampleEncoding = TerrainHeightSampleEncoding.GameDataRawUInt16,
            Samples = Enumerable.Repeat((float)TerrainHeightEncoding.WorldHeightToGameDataRaw(1.5f), 33 * 33).ToArray()
        };

        var regenerator = new LocalTerrainDerivativeRegenerator();
        var state = regenerator.RegenerateAroundBounds(project, map, "Mesh", new(2, 0, 0),
            new(0, 0, 32, 32), new(0, 0, 32, 32));
        state.UseCoarseHeightMap = true;
        var write = Assert.Single(regenerator.GenerateMeshPageWrites(project, map, state, true, 99));
        var page = new MeshPageReader().Read(write.TargetPath, write.Key, write.XmlText, false);
        var seamVertices = Assert.Single(page.Clusters).Vertices.Where(v => v.Raw0 == 0 && v.Raw2 == 0).ToList();

        Assert.Equal(2, seamVertices.Count);
        Assert.Equal(1.5f, seamVertices.Max(v => v.WorldHeight), 5);
        Assert.Equal(40f / 256f, seamVertices.Max(v => v.WorldHeight) - seamVertices.Min(v => v.WorldHeight), 5);

        project.MeshPages[page.Key] = page;
        var sampler = MeshBackedHeightSampler.Create(project, 2)!;
        Assert.True(sampler.TrySample(0, 0, out var sampled));
        Assert.Equal(1.5f, sampled, 5);
    }

    [Fact]
    public void Conservative_Rebuild_Preserves_Existing_Skirt_Depth_Exactly()
    {
        var project = FlatProject();
        var cluster = Assert.Single(project.MeshPages[new(2, 0, 0)].Clusters);
        cluster.Vertices.Add(new MeshVertex
        {
            Raw0 = cluster.Vertices[0].Raw0,
            Raw2 = cluster.Vertices[0].Raw2,
            Raw1 = MeshVertex.EncodeWorldHeight(cluster.Vertices[0].WorldHeight - .5f)
        });
        cluster.Indices.AddRange(new[] { 4, 0, 1 });
        var map = new CoarseHeightMap
        {
            Width = 33, Height = 33, SampleEncoding = TerrainHeightSampleEncoding.GameDataRawUInt16,
            Samples = Enumerable.Repeat((float)TerrainHeightEncoding.WorldHeightToGameDataRaw(1.5f), 33 * 33).ToArray()
        };

        var regenerator = new LocalTerrainDerivativeRegenerator();
        var state = regenerator.RegenerateAroundBounds(project, map, "Mesh", new(2, 0, 0),
            new(0, 0, 32, 32), new(0, 0, 32, 32));
        state.UseCoarseHeightMap = true;
        var write = Assert.Single(regenerator.GenerateMeshPageWrites(project, map, state, true, 99));
        var page = new MeshPageReader().Read(write.TargetPath, write.Key, write.XmlText, false);
        var seamVertices = Assert.Single(page.Clusters).Vertices.Where(v => v.Raw0 == 0 && v.Raw2 == 0).ToList();

        Assert.Equal(2, seamVertices.Count);
        Assert.Equal(.5f, seamVertices.Max(v => v.WorldHeight) - seamVertices.Min(v => v.WorldHeight), 5);
    }

    [Fact]
    public void CoastContour_Separates_Hole_And_Exterior_And_Points_Outward()
    {
        var mask = Enumerable.Repeat(true, 25).ToArray();
        mask[12] = false;
        var loops = FootprintCoast.Build(mask, 5, 5, 1280, 1280);
        Assert.Equal(2, loops.Count);
        foreach (var coast in loops)
        foreach (var span in coast.Slice(0, coast.Length))
        {
            var mid = (span.Start + span.End) * .5f;
            var sea = mid + span.Outward * 8;
            var land = mid - span.Outward * 8;
            Assert.True(ActiveMapGridMaskCodec.IsWorldPointActive(mask, new Int2(5, 5), land.X, land.Y, 1280, 1280));
            Assert.False(sea.X >= 0 && sea.Y >= 0 && sea.X < 1280 && sea.Y < 1280 &&
                ActiveMapGridMaskCodec.IsWorldPointActive(mask, new Int2(5, 5), sea.X, sea.Y, 1280, 1280));
        }
    }

    [Fact]
    public void FullValidation_Uses_Consolidated_Heightmap_For_All_Lods_Including_Below_Anchor()
    {
        var project = FlatProject();
        foreach (var lod in new[] { 3, 8 })
        {
            var page = FlatProject().MeshPages[new(2, 0, 0)];
            page.Key = new(lod, 0, 0);
            page.OriginalRelativePath = $"meshes/{lod}/0x0.xml";
            project.MeshPages[page.Key] = page;
        }
        var map = new CoarseHeightMap
        {
            Width = 33, Height = 33, SampleEncoding = TerrainHeightSampleEncoding.GameDataRawUInt16,
            Samples = Enumerable.Repeat((float)TerrainHeightEncoding.WorldHeightToGameDataRaw(1.25f), 33 * 33).ToArray()
        };
        var regenerator = new LocalTerrainDerivativeRegenerator();
        var state = regenerator.RegenerateAroundBounds(project, map, "Mesh", new(3, 0, 0),
            new(0, 0, 32, 32), new(0, 0, 32, 32));
        state.AnchorMeshLod = 3;
        state.UseCoarseHeightMap = true;
        var writes = regenerator.GenerateMeshPageWrites(project, map, state, true, 99);
        Assert.Equal(new[] { 2, 3, 8 }, writes.Select(w => w.Key.Lod));
        foreach (var write in writes)
        {
            var page = new MeshPageReader().Read(write.TargetPath, write.Key, write.XmlText, false);
            Assert.All(page.Clusters.SelectMany(c => c.Vertices), v => Assert.Equal(1.25f, v.WorldHeight, 5));
        }
    }

    [Fact]
    public void ContourSlice_Wraps_And_Follows_Corners_Without_A_Chord()
    {
        var coast = Assert.Single(FootprintCoast.Build(new[] { true }, 1, 1, 100, 100));
        var spans = coast.Slice(coast.Length - 50, 180);
        Assert.Equal(180, spans.Sum(s => s.Length), 4);
        Assert.Equal(3, spans.Count);
        Assert.All(spans, s => Assert.True(s.Start.X == s.End.X || s.Start.Y == s.End.Y));
        Assert.Equal(spans[0].End, spans[1].Start);
        Assert.Equal(spans[1].End, spans[2].Start);
    }

    [Fact]
    public void DiagonalIslands_Remain_Two_Independent_Contours()
    {
        var contours = FootprintCoast.Build(new[] { true, false, false, true }, 2, 2, 200, 200);
        Assert.Equal(2, contours.Count);
        Assert.All(contours, c => Assert.Equal(400, c.Length));
    }

    [Fact]
    public void OutwardProfile_Preserves_Interior_And_Creates_A_Dry_Plateau()
    {
        var profile = new OutwardBeachProfile();
        var spans = new[] { new CoastSpan(new(100, 100), new(1100, 100), new(0, -1)) };
        Assert.False(profile.TryGetDistance(spans, new(500, 240), out _));
        Assert.True(profile.TryGetDistance(spans, new(500, -100), out var distance));
        Assert.Equal(200, distance);
        Assert.Equal(profile.BeachWorldHeight, profile.Height(distance, -8));
        Assert.Equal(32d, profile.InnerBlendWidth);
        Assert.Equal(3f, profile.Height(-32, 3f));
        Assert.InRange(profile.Height(-16, 3f), profile.BeachWorldHeight, 3f);
        Assert.False(profile.TryGetDistance(spans, new(500, 133), out _));
        Assert.Equal(-8f, profile.Height(640, -8f));
    }

    [Fact]
    public void Long_Coast_Gets_One_Overlapping_Gameplay_Anchor_Per_Tile()
    {
        var span = new CoastSpan(new(0, 0), new(1000, 0), new(0, -1));
        var anchors = span.BuildGameplayAnchorWindows();
        Assert.Equal(32, anchors.Count);
        Assert.All(anchors, anchor => Assert.Equal(512d, anchor.Span.Length, 3));
        Assert.All(anchors, anchor => Assert.InRange(anchor.Anchor.X, anchor.Span.Start.X, anchor.Span.End.X));
        Assert.InRange(anchors.Zip(anchors.Skip(1), (a, b) => b.Anchor.X - a.Anchor.X).Max(), 0, 32);
        Assert.True(anchors[0].Span.End.X > anchors[1].Span.Start.X);
    }

    [Fact]
    public void Overlapping_Gameplay_Curves_Merge_Back_Into_One_Logical_Beach()
    {
        var zones = GeneratedBeachZones.MergeCollinear(new[]
        {
            new GeneratedBeachZone(-10, new(0, 0), new(20, 0), new(0, -1), .4f),
            new GeneratedBeachZone(-11, new(10, 0), new(30, 0), new(0, -1), .4f),
            new GeneratedBeachZone(-12, new(25, 0), new(40, 0), new(0, -1), .4f)
        });
        var zone = Assert.Single(zones);
        Assert.Equal(new System.Numerics.Vector2(0, 0), zone.Start);
        Assert.Equal(new System.Numerics.Vector2(40, 0), zone.End);
    }
}
