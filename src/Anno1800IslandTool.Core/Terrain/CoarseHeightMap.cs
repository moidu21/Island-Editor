namespace Anno1800IslandTool.Core.Terrain;

public sealed class CoarseHeightMap
{
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>
    /// Valeurs de hauteur telles que conservées dans le modèle.
    /// Pour les fichiers GameData lus depuis le jeu : uint16 texte, encodage GameDataRawUInt16.
    /// </summary>
    public float[] Samples { get; set; } = Array.Empty<float>();

    public TerrainHeightSampleEncoding SampleEncoding { get; set; } = TerrainHeightSampleEncoding.Unknown;

    public float GetWorldHeightSample(int x, int y)
    {
        if (Width <= 0 || Height <= 0 || Samples.Length == 0)
        {
            return 0f;
        }

        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        return TerrainHeightEncoding.CoarseSampleToWorldHeight(this, Samples[(y * Width) + x]);
    }
}
