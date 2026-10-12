using System.Globalization;
using System.Text;
using Anno1800IslandTool.Pipeline.Services;

namespace Anno1800IslandTool.UI;

public partial class Terrain3DWindow
{
    private string ApplyTreesFromGpu(IReadOnlyList<GpuTreeInstance> trees)
    {
        if (trees.Count == 0) return Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucun arbre à ajouter.");
        var fileNames = _project.Rd3d.PropGrid.FileNames.ToList();
        var synchronizer = new Anno1800IslandTool.Pipeline.Services.MaterialSetEnvironmentSynchronizer();
        var compatiblePaths = synchronizer.GetCompatibleTreePaths(_project, out var biomeName).ToList();
        var treeIndexes = new List<int>();
        foreach (var path in compatiblePaths)
        {
            var existing = fileNames.FindIndex(candidate => string.Equals(
                candidate.Replace('\\', '/'), path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (existing < 0)
            {
                existing = fileNames.Count;
                fileNames.Add(path);
            }
            treeIndexes.Add(existing);
        }
        if (treeIndexes.Count == 0) throw new InvalidOperationException(Anno1800IslandTool.Core.Localization.TextCatalog.F($"Aucun arbre compatible n’est disponible pour le biome {biomeName}."));

        var gridWidth = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : _project.Root.MapSize * 32d;
        var gridHeight = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : _project.Root.MapSize * 32d;
        var mapSize = Math.Max(1, _project.Root.MapSize);
        var sourceLod = _project.MeshPages.Keys
            .Where(key => key.Lod >= 2)
            .Select(key => key.Lod)
            .DefaultIfEmpty(-1)
            .Min();
        var heightSampler = MeshBackedHeightSampler.Create(_project, sourceLod);
        var instances = _project.Rd3d.PropGrid.Instances.Select(instance => instance.RawNoneXml).ToList();
        for (var index = 0; index < trees.Count; index++)
        {
            var tree = trees[index];
            var fileIndex = treeIndexes[Math.Abs(HashCode.Combine((int)tree.X, (int)tree.Z, index)) % treeIndexes.Count];
            var x = tree.X / gridWidth * mapSize;
            var z = tree.Z / gridHeight * mapSize;
            var worldHeight = tree.WorldHeight;
            if (heightSampler is not null && heightSampler.TrySample(tree.X, tree.Z, out var sampledHeight))
                worldHeight = sampledHeight;
            var sin = Math.Sin(tree.RotationRadians * .5d);
            var cos = Math.Cos(tree.RotationRadians * .5d);
            var sb = new StringBuilder();
            sb.AppendLine("<None>");
            sb.AppendLine($"  <Index>{fileIndex}</Index>");
            sb.AppendLine($"  <Position>{TreeNumber(x)} {TreeNumber(worldHeight)} {TreeNumber(z)}</Position>");
            sb.AppendLine($"  <Rotation>0 {TreeNumber(sin)} 0 {TreeNumber(cos)}</Rotation>");
            sb.AppendLine($"  <Scale>{TreeNumber(tree.Scale)} {TreeNumber(tree.Scale)} {TreeNumber(tree.Scale)}</Scale>");
            sb.AppendLine("  <Color>1 1 1 1</Color>");
            // Position.Y is already sampled on the terrain. Keep the native tree transform
            // stable across mesh/impostor LOD changes; terrain validation updates the anchor.
            sb.AppendLine("  <AdaptTerrainHeight>False</AdaptTerrainHeight>");
            sb.Append("</None>");
            instances.Add(sb.ToString());
        }

        _editor.ReplacePropGrid(_project, fileNames, instances);
        return string.Format(CultureInfo.CurrentCulture,
            UiLocalizationManager.T("{0:N0} arbres vivants du biome {1} ajoutés avec leur altitude réelle pour une visibilité stable."),
            trees.Count, biomeName);
    }

    private static string TreeNumber(double value)
        => value.ToString("0.######", CultureInfo.InvariantCulture).Replace('.', ',');
}
