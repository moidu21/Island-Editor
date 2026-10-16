using System.Globalization;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Enums;
using Anno1800IslandTool.Core.Terrain;

namespace Anno1800IslandTool.Formats.Readers;

public sealed class MeshPageReader
{
    private static readonly Regex ClusterRegex = new(
        @"<None>\s*<Indices>\s*(?<indices>.*?)\s*</Indices>\s*<Vertices>\s*(?<vertices>.*?)\s*</Vertices>\s*<UsedMaterialBits>\s*(?<bits>-?\d+)\s*</UsedMaterialBits>\s*(?<ground><GroundObjects(?:\s*/>|>.*?</GroundObjects>))\s*</None>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex GroundObjectRegex = new(
        @"<None>\s*<ID>\s*(?<id>-?\d+)\s*</ID>\s*<FileName>\s*(?<file>[^<\r\n]+)\s*</FileName>\s*<PositionsXZ>\s*(?<pos>[^<\r\n]+)\s*</PositionsXZ>\s*<RotationsY>\s*(?<rot>[^<\r\n]+)\s*</RotationsY>\s*<Coverages>\s*(?<cov>[^<\r\n]+)\s*</Coverages>\s*</None>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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

        foreach (Match match in ClusterRegex.Matches(text))
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
                    Raw0 = vertexValues[i],
                    Raw1 = vertexValues[i + 1],
                    Raw2 = vertexValues[i + 2],
                    Raw3 = vertexValues[i + 3],
                    Raw4 = vertexValues[i + 4],
                    Raw5 = vertexValues[i + 5],
                    Raw6 = vertexValues[i + 6],
                    Raw7 = vertexValues[i + 7]
                });
            }

            foreach (Match objectMatch in GroundObjectRegex.Matches(match.Groups["ground"].Value))
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
        var vertexCount = page.Clusters.Sum(c => c.Vertices.Count);
        var indexCount = page.Clusters.Sum(c => c.Indices.Count);
        var groundCount = page.Clusters.Sum(c => c.GroundObjects.Count);
        var hasAnyVerticesTag = text.Contains("<Vertices>", StringComparison.OrdinalIgnoreCase);

        page.HasDecodedPayload = page.ParsedClusterCount > 0;
        page.HasEditablePayload = vertexCount > 0;
        page.ProbableHeightComponentIndex = null;
        page.VertexLayoutHypothesis = string.Empty;
        page.CoordinateRanges = string.Empty;

        if (page.HasEditablePayload)
        {
            InferVertexLayout(page);

            page.DecodeStatus = "Décodée";
            page.DecodeNotes =
                Anno1800IslandTool.Core.Localization.TextCatalog.F($"Clusters décodés : {page.ParsedClusterCount}/{page.DeclaredClusterCount}, vertices={vertexCount}, indices={indexCount}, ground={groundCount}. ") +
                Anno1800IslandTool.Core.Localization.TextCatalog.T("Hypothèse de layout : [X, Y, Z, A, B, C, D, E] avec Y brut à l'index 1, DecodedHeight=((ushort)Y)-32767, et A..E comme attributs packés de vertex plutôt que de purs poids de matériau.");
            return;
        }

        if (page.HasDecodedPayload && hasAnyVerticesTag)
        {
            page.DecodeStatus = "Vide";
            page.DecodeNotes = Anno1800IslandTool.Core.Localization.TextCatalog.F($"La page a été lue mais ne contient aucun vertex exploitable. Clusters={page.ParsedClusterCount}, ground={groundCount}.");
            return;
        }

        var hasVertexNumbers = Regex.IsMatch(text, @"<Vertices>\s*[-0-9]", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (page.ContainsStructuredMeshPayload || hasVertexNumbers)
        {
            page.DecodeStatus = "Non décodée";
            page.DecodeNotes = Anno1800IslandTool.Core.Localization.TextCatalog.F($"Structure mesh détectée mais aucun cluster exploitable n'a été reconstruit. Clusters attendus ≈ {page.DeclaredClusterCount}.");
            return;
        }

        page.DecodeStatus = "Inconnue";
        page.DecodeNotes = Anno1800IslandTool.Core.Localization.TextCatalog.T("Le contenu de page ne ressemble pas à un payload mesh interprété standard, ou le détail des pages n'a pas été chargé.");
    }

    private static void InferVertexLayout(MeshPage page)
    {
        page.VertexStride = 8;
        var minX = int.MaxValue;
        var maxX = int.MinValue;
        var minRawHeight = int.MaxValue;
        var maxRawHeight = int.MinValue;
        var minDecodedHeight = int.MaxValue;
        var maxDecodedHeight = int.MinValue;
        var minZ = int.MaxValue;
        var maxZ = int.MinValue;
        foreach (var cluster in page.Clusters)
        {
            foreach (var vertex in cluster.Vertices)
            {
                minX = Math.Min(minX, vertex.X);
                maxX = Math.Max(maxX, vertex.X);
                minRawHeight = Math.Min(minRawHeight, vertex.Raw1);
                maxRawHeight = Math.Max(maxRawHeight, vertex.Raw1);
                minDecodedHeight = Math.Min(minDecodedHeight, vertex.DecodedHeight);
                maxDecodedHeight = Math.Max(maxDecodedHeight, vertex.DecodedHeight);
                minZ = Math.Min(minZ, vertex.Z);
                maxZ = Math.Max(maxZ, vertex.Z);
            }
        }

        page.ProbableHeightComponentIndex = 1;
        page.VertexLayoutHypothesis = Anno1800IslandTool.Core.Localization.TextCatalog.T("[X, Y, Z, A, B, C, D, E] ; Y brut index 1 ; DecodedHeight=((ushort)Y)-32767 ; A..E attributs packés à préserver");
        page.CoordinateRanges =
            $"X={minX}..{maxX}, Yraw={minRawHeight}..{maxRawHeight}, Ydecoded={minDecodedHeight}..{maxDecodedHeight}, Z={minZ}..{maxZ}";
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
    {
        var result = new List<int>(Math.Max(4, value.Length / 6));
        var span = value.AsSpan();
        var index = 0;
        while (index < span.Length)
        {
            while (index < span.Length && span[index] != '-' && !char.IsAsciiDigit(span[index]))
            {
                index++;
            }
            if (index >= span.Length)
            {
                break;
            }

            var negative = span[index] == '-';
            if (negative)
            {
                index++;
            }
            if (index >= span.Length || !char.IsAsciiDigit(span[index]))
            {
                continue;
            }

            var parsed = 0;
            while (index < span.Length && char.IsAsciiDigit(span[index]))
            {
                parsed = unchecked((parsed * 10) + (span[index] - '0'));
                index++;
            }
            result.Add(negative ? -parsed : parsed);
        }

        return result;
    }

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

