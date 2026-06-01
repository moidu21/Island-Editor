using System.Globalization;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Enums;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Formats.Readers;

public sealed class MeshPageReader
{
    public MeshPage Read(string relativePath, TerrainPageKey key, string text, bool retainRawText = true)
    {
        var result = new MeshPage
        {
            Key = key,
            OriginalRelativePath = relativePath,
            DeclaredWidth = TryReadIntTag(text, "width"),
            DeclaredHeight = TryReadIntTag(text, "height"),
            VertexStride = 8
        };

        result.DeclaredClusterCount = result.DeclaredWidth > 0 && result.DeclaredHeight > 0
            ? result.DeclaredWidth * result.DeclaredHeight
            : Regex.Matches(text, @"<UsedMaterialBits>", RegexOptions.IgnoreCase).Count;
        result.ContainsStructuredMeshPayload = text.Contains("<Meshes>", StringComparison.OrdinalIgnoreCase) ||
                                               text.Contains("<UsedMaterialBits>", StringComparison.OrdinalIgnoreCase);

        foreach (Match match in Regex.Matches(
                     text,
                     @"<None>\s*<Indices>\s*(?<indices>.*?)\s*</Indices>\s*<Vertices>\s*(?<vertices>.*?)\s*</Vertices>\s*<UsedMaterialBits>\s*(?<bits>-?\d+)\s*</UsedMaterialBits>\s*(?<ground><GroundObjects(?:\s*/>|>.*?</GroundObjects>))\s*</None>",
                     RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var cluster = new MeshCluster
            {
                UsedMaterialBits = ParseSignedMaterialBits(match.Groups["bits"].Value)
            };

            cluster.Indices.AddRange(ParseInts(match.Groups["indices"].Value));

            var vertexValues = ParseInts(match.Groups["vertices"].Value);
            for (var i = 0; i + 7 < vertexValues.Count; i += 8)
            {
                cluster.Vertices.Add(new MeshVertex
                {
                    RawValues = vertexValues.Skip(i).Take(8).ToArray()
                });
            }

            foreach (Match objectMatch in Regex.Matches(
                         match.Groups["ground"].Value,
                         @"<None>\s*<ID>\s*(?<id>-?\d+)\s*</ID>\s*<FileName>\s*(?<file>[^<\r\n]+)\s*</FileName>\s*<PositionsXZ>\s*(?<pos>[^<\r\n]+)\s*</PositionsXZ>\s*<RotationsY>\s*(?<rot>[^<\r\n]+)\s*</RotationsY>\s*<Coverages>\s*(?<cov>[^<\r\n]+)\s*</Coverages>\s*</None>",
                         RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var pos = ParseFloats(objectMatch.Groups["pos"].Value);
                var rot = ParseFloats(objectMatch.Groups["rot"].Value);
                var cov = ParseFloats(objectMatch.Groups["cov"].Value);
                var fileName = objectMatch.Groups["file"].Value.Trim();

                cluster.GroundObjects.Add(new GroundObjectInstance
                {
                    Id = int.TryParse(objectMatch.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var groundObjectId) ? groundObjectId : 0,
                    ObjectFileName = fileName,
                    Kind = ClassifyGroundObject(fileName),
                    PositionXZ = pos.Count >= 2 ? new Float2(pos[0], pos[1]) : default,
                    RotationY = rot.Count >= 1 ? rot[0] : 0f,
                    Coverage = cov.Count >= 1 ? cov[0] : 0f
                });
            }

            result.Clusters.Add(cluster);
        }

        result.ParsedClusterCount = result.Clusters.Count;
        ApplyDiagnostics(result, text);
        if (retainRawText)
        {
            result.RawUnknownFields["__raw"] = text;
        }
        return result;
    }

    private static void ApplyDiagnostics(MeshPage page, string text)
    {
        var allVertices = page.Clusters.SelectMany(c => c.Vertices).ToList();
        var vertexCount = allVertices.Count;
        var indexCount = page.Clusters.Sum(c => c.Indices.Count);
        var groundCount = page.Clusters.Sum(c => c.GroundObjects.Count);
        var hasVertexNumbers = Regex.IsMatch(text, @"<Vertices>\s*[-0-9]", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var hasAnyVerticesTag = text.Contains("<Vertices>", StringComparison.OrdinalIgnoreCase);

        page.HasDecodedPayload = page.ParsedClusterCount > 0;
        page.HasEditablePayload = vertexCount > 0;
        page.ProbableHeightComponentIndex = null;
        page.VertexLayoutHypothesis = string.Empty;
        page.CoordinateRanges = string.Empty;

        if (page.HasEditablePayload)
        {
            InferVertexLayout(page, allVertices);

            page.DecodeStatus = "Décodée";
            page.DecodeNotes =
                $"Clusters décodés : {page.ParsedClusterCount}/{page.DeclaredClusterCount}, vertices={vertexCount}, indices={indexCount}, ground={groundCount}. " +
                "Hypothèse de layout : [X, Y, Z, A, B, C, D, E] avec Y brut à l'index 1, DecodedHeight=((ushort)Y)-32767, et A..E comme attributs packés de vertex plutôt que de purs poids de matériau.";
            return;
        }

        if (page.HasDecodedPayload && hasAnyVerticesTag)
        {
            page.DecodeStatus = "Vide";
            page.DecodeNotes = $"La page a été lue mais ne contient aucun vertex exploitable. Clusters={page.ParsedClusterCount}, ground={groundCount}.";
            return;
        }

        if (page.ContainsStructuredMeshPayload || hasVertexNumbers)
        {
            page.DecodeStatus = "Non décodée";
            page.DecodeNotes = $"Structure mesh détectée mais aucun cluster exploitable n'a été reconstruit. Clusters attendus ≈ {page.DeclaredClusterCount}.";
            return;
        }

        page.DecodeStatus = "Inconnue";
        page.DecodeNotes = "Le contenu de page ne ressemble pas à un payload mesh interprété standard, ou le détail des pages n'a pas été chargé.";
    }

    private static void InferVertexLayout(MeshPage page, IReadOnlyList<MeshVertex> vertices)
    {
        page.VertexStride = 8;

        var xs = vertices.Select(v => (float)v.Raw0).ToArray();
        var rawHeights = vertices.Select(v => (float)v.Raw1).ToArray();
        var zs = vertices.Select(v => (float)v.Raw2).ToArray();
        var decodedHeights = vertices.Select(v => (float)v.DecodedHeight).ToArray();

        page.ProbableHeightComponentIndex = 1;
        page.VertexLayoutHypothesis = "[X, Y, Z, A, B, C, D, E] ; Y brut index 1 ; DecodedHeight=((ushort)Y)-32767 ; A..E attributs packés à préserver";
        page.CoordinateRanges =
            $"X={FormatRange(xs)}, Yraw={FormatRange(rawHeights)}, Ydecoded={FormatRange(decodedHeights)}, Z={FormatRange(zs)}";
    }

    private static string FormatRange(IEnumerable<float> values)
    {
        var arr = values.ToArray();
        if (arr.Length == 0)
        {
            return "n/a";
        }

        return $"{arr.Min().ToString("0.###", CultureInfo.InvariantCulture)}..{arr.Max().ToString("0.###", CultureInfo.InvariantCulture)}";
    }

    private static int TryReadIntTag(string text, string tag)
    {
        var match = Regex.Match(text, $@"<{tag}>\s*(?<value>\d+)\s*</{tag}>", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }


    private static int ParseSignedMaterialBits(string value)
    {
        // v31 : les pages vanilla écrivent souvent UsedMaterialBits comme int32 signé
        // (ex. bit 31 actif => valeur négative). Un parse ulong transforme ces valeurs
        // en 0 et casse les LOD distants côté shader/material coverage.
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed))
        {
            return signed;
        }

        if (uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsigned))
        {
            return unchecked((int)unsigned);
        }

        return 0;
    }

    private static List<int> ParseInts(string value)
        => Regex.Matches(value, @"-?\d+")
            .Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture))
            .ToList();

    private static List<float> ParseFloats(string value)
    {
        var values = new List<float>();

        foreach (Match match in Regex.Matches(value, @"-?\d+(?:[.,]\d+)?"))
        {
            if (float.TryParse(match.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                values.Add(parsed);
            }
        }

        return values;
    }

    private static GroundObjectKind ClassifyGroundObject(string fileName)
    {
        if (fileName.Contains("vegetation", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("grass", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("flower", StringComparison.OrdinalIgnoreCase))
        {
            return GroundObjectKind.Vegetation;
        }

        if (fileName.Contains("prop", StringComparison.OrdinalIgnoreCase))
        {
            return GroundObjectKind.Prop;
        }

        return GroundObjectKind.Decoration;
    }
}
