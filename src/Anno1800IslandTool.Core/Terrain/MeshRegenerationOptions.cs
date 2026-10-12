namespace Anno1800IslandTool.Core.Terrain;

public sealed class MeshRawAttributePreset
{
    public int Raw3 { get; set; }
    public int Raw4 { get; set; }
    public int Raw5 { get; set; }
    public int Raw6 { get; set; }
    public int Raw7 { get; set; }

    public override string ToString() => $"[{Raw3}, {Raw4}, {Raw5}, {Raw6}, {Raw7}]";
}

public sealed class MeshRegenerationOptions
{
    public bool RebuildTopology { get; set; }
    public int MaxVerticesPerAxis { get; set; } = 48;
    public int HeightSmoothingPasses { get; set; } = 1;
    public bool PreserveNearestRawAttributes { get; set; } = true;

    /// <summary>
    /// v32 : le rebuild topologique ne doit plus recréer une grille pleine par cluster.
    /// Cette valeur limite le nombre de vertices générés à un budget proche de la vanilla.
    /// 1.15 signifie environ +15% maximum avant padding, pour absorber les retouches locales
    /// sans transformer les LOD distants en grilles 24/48².
    /// </summary>
    public double AdaptiveVertexBudgetMultiplier { get; set; } = 1.15d;

    /// <summary>
    /// v32 : petite marge par cluster pour éviter une perte de détail dans les clusters très pauvres.
    /// </summary>
    public int AdaptiveVertexBudgetPadding { get; set; } = 8;

    /// <summary>
    /// v32 : conserve l'empreinte triangulée vanilla du cluster. Sans cela, les clusters de bord
    /// sont remplis en rectangles complets et produisent de gros carrés visibles derrière les plages.
    /// </summary>
    public bool PreserveOriginalFootprint { get; set; } = true;

    /// <summary>
    /// v31 : désactivé par défaut. Raw4..Raw7 sont consommés directement par le shader
    /// comme normal/tangent + morceaux d'UV ; les recalculer avec un mauvais repère casse
    /// le rendu. Le mode sûr pour un aplatissement conserve ces attributs et ne change que Y.
    /// </summary>
    public bool RebuildSurfaceAttributes { get; set; } = false;

    public MeshRawAttributePreset? RawAttributePreset { get; set; }
}
