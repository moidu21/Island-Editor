namespace Anno1800IslandTool.Core.Terrain;

/// <summary>
/// Rôle fonctionnel d'une page mesh.
/// Important pour Anno 1800 : les LOD0/LOD1 observés dans les îles de base servent de
/// couverture, métadonnées et ground objects. Ils ne doivent pas être traités comme des
/// pages de géométrie éditable, même si un fichier existe.
/// </summary>
public enum MeshPageRole
{
    Unknown = 0,
    MetadataCoverage = 1,
    ProbableEditableGeometry = 2,
    EditableGeometry = 3,
    DecodedGeometryNotEditable = 4
}
