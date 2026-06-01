namespace Anno1800IslandTool.Core.Performance;

/// <summary>
/// Choix mémoire/performance pour les grosses îles.
/// v40.4 : évite que l'import détaillé garde inutilement tout le XML des pages
/// mesh/normalmap en RAM lorsque l'utilisateur privilégie la stabilité mémoire.
/// </summary>
public enum TerrainMemoryMode
{
    Balanced = 0,
    LowMemory = 1,
    Speed = 2
}
