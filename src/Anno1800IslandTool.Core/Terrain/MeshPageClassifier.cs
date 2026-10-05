namespace Anno1800IslandTool.Core.Terrain;

/// <summary>
/// Classification centralisée des pages mesh.
/// 
/// Règle v22 : LOD0/LOD1 sont toujours protégées et considérées comme
/// metadata/coverage/ground objects. Les opérations d'édition et de régénération mesh ne
/// peuvent cibler que LOD >= 2 et seulement les pages ayant un payload éditable avec vertices.
/// </summary>
public static class MeshPageClassifier
{
    public const int FirstEditableGeometryLod = 2;
    // LOD9+ mesh files remain outside the geometry editor. Their RD3D coverage,
    // level dimensions and height bounds are synchronized separately with the canvas.
    public const int LastEditableGeometryLod = 8;

    public static bool IsProtectedMetadataLod(TerrainPageKey key)
        => key.Lod < FirstEditableGeometryLod;

    public static bool IsUnsupportedFarMetadataLod(TerrainPageKey key)
        => key.Lod > LastEditableGeometryLod;

    public static bool IsGeometryCandidateLod(TerrainPageKey key)
        => key.Lod >= FirstEditableGeometryLod && key.Lod <= LastEditableGeometryLod;

    public static bool HasVertices(MeshPage? page)
        => page is not null && page.Clusters.Any(cluster => cluster.Vertices.Count > 0);

    public static bool HasGroundObjects(MeshPage? page)
        => page is not null && page.Clusters.Any(cluster => cluster.GroundObjects.Count > 0);

    public static bool HasAnyDecodedPayload(MeshPage? page)
        => page is not null &&
           (page.HasDecodedPayload ||
            page.HasEditablePayload ||
            page.ContainsStructuredMeshPayload ||
            page.ParsedClusterCount > 0 ||
            page.Clusters.Count > 0);

    public static bool CanEditGeometry(MeshPage? page)
        => page is not null &&
           IsGeometryCandidateLod(page.Key) &&
           page.HasEditablePayload &&
           HasVertices(page);

    public static MeshPageRole Classify(TerrainPageKey key, MeshPage? page, bool treatUnknownGeometryLodAsProbable = true)
    {
        if (IsProtectedMetadataLod(key) || IsUnsupportedFarMetadataLod(key))
        {
            return MeshPageRole.MetadataCoverage;
        }

        if (CanEditGeometry(page))
        {
            return MeshPageRole.EditableGeometry;
        }

        if (HasVertices(page))
        {
            return MeshPageRole.DecodedGeometryNotEditable;
        }

        if (page is null || !HasAnyDecodedPayload(page))
        {
            return treatUnknownGeometryLodAsProbable
                ? MeshPageRole.ProbableEditableGeometry
                : MeshPageRole.Unknown;
        }

        return MeshPageRole.MetadataCoverage;
    }

    public static bool IsEditableOrProbableGeometry(TerrainPageKey key, MeshPage? page, bool treatUnknownGeometryLodAsProbable = true)
    {
        var role = Classify(key, page, treatUnknownGeometryLodAsProbable);
        return role is MeshPageRole.EditableGeometry or MeshPageRole.ProbableEditableGeometry or MeshPageRole.DecodedGeometryNotEditable;
    }

    public static string GetRoleLabel(MeshPageRole role)
        => role switch
        {
            MeshPageRole.MetadataCoverage => "métadonnées/coverage/ground objects",
            MeshPageRole.ProbableEditableGeometry => "géométrie probable (payload non chargé)",
            MeshPageRole.EditableGeometry => "géométrie éditable",
            MeshPageRole.DecodedGeometryNotEditable => "géométrie décodée non éditable",
            _ => "inconnu"
        };

    public static string BuildReason(TerrainPageKey key, MeshPage? page)
    {
        var role = Classify(key, page);
        if (IsProtectedMetadataLod(key))
        {
            var ground = HasGroundObjects(page) ? ", ground objects présents" : string.Empty;
            var vertices = HasVertices(page) ? ", vertices ignorés par sécurité" : string.Empty;
            return $"LOD{key.Lod} protégé : traité comme {GetRoleLabel(role)}{ground}{vertices}.";
        }

        if (IsUnsupportedFarMetadataLod(key))
        {
            return $"LOD{key.Lod} conservé vanilla : hors plage éditable v42.9 LOD{FirstEditableGeometryLod}..LOD{LastEditableGeometryLod}.";
        }

        return role switch
        {
            MeshPageRole.EditableGeometry => $"Payload mesh décodé, LOD {FirstEditableGeometryLod}..{LastEditableGeometryLod}, vertices présents : page modifiable.",
            MeshPageRole.ProbableEditableGeometry => $"LOD {FirstEditableGeometryLod}..{LastEditableGeometryLod} mais payload non chargé : page candidate, diagnostic uniquement tant que le payload n'est pas lu.",
            MeshPageRole.DecodedGeometryNotEditable => $"LOD {FirstEditableGeometryLod}..{LastEditableGeometryLod} avec vertices, mais payload non marqué éditable : modification bloquée.",
            MeshPageRole.MetadataCoverage => $"LOD {FirstEditableGeometryLod}..{LastEditableGeometryLod} sans vertices éditables : page traitée comme métadonnées/coverage.",
            _ => "Statut mesh inconnu."
        };
    }
}
