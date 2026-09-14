using System.Globalization;
using System.Text;

namespace Anno1800IslandTool.UI;

public partial class Terrain3DWindow
{
    private string ApplyTreesFromGpu(IReadOnlyList<GpuTreeInstance> trees)
    {
        if (trees.Count == 0) return "Aucun arbre à ajouter.";
        var fileNames = _project.Rd3d.PropGrid.FileNames.ToList();
        var treeIndexes = fileNames
            .Select((path, index) => (path, index))
            .Where(item => item.path.Contains("vegetation/trees/moderate", StringComparison.OrdinalIgnoreCase)
                && (item.path.EndsWith(".prp", StringComparison.OrdinalIgnoreCase) || item.path.EndsWith(".brp", StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.index)
            .ToList();
        if (treeIndexes.Count == 0)
        {
            fileNames.Add("data/graphics/props/terrain_props/vegetation/trees/moderate/tree_moderate_01_var0.prp");
            treeIndexes.Add(fileNames.Count - 1);
        }

        var gridWidth = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : _project.Root.MapSize * 32d;
        var gridHeight = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : _project.Root.MapSize * 32d;
        var mapSize = Math.Max(1, _project.Root.MapSize);
        var instances = _project.Rd3d.PropGrid.Instances.Select(instance => instance.RawNoneXml).ToList();
        for (var index = 0; index < trees.Count; index++)
        {
            var tree = trees[index];
            var fileIndex = treeIndexes[Math.Abs(HashCode.Combine((int)tree.X, (int)tree.Z, index)) % treeIndexes.Count];
            var x = tree.X / gridWidth * mapSize;
            var z = tree.Z / gridHeight * mapSize;
            var sin = Math.Sin(tree.RotationRadians * .5d);
            var cos = Math.Cos(tree.RotationRadians * .5d);
            var sb = new StringBuilder();
            sb.AppendLine("<None>");
            sb.AppendLine($"  <Index>{fileIndex}</Index>");
            sb.AppendLine($"  <Position>{TreeNumber(x)} {TreeNumber(tree.WorldHeight)} {TreeNumber(z)}</Position>");
            sb.AppendLine($"  <Rotation>0 {TreeNumber(sin)} 0 {TreeNumber(cos)}</Rotation>");
            sb.AppendLine($"  <Scale>{TreeNumber(tree.Scale)} {TreeNumber(tree.Scale)} {TreeNumber(tree.Scale)}</Scale>");
            sb.AppendLine("  <Color>1 1 1 1</Color>");
            sb.AppendLine("  <AdaptTerrainHeight>True</AdaptTerrainHeight>");
            sb.Append("</None>");
            instances.Add(sb.ToString());
        }

        _editor.ReplacePropGrid(_project, fileNames, instances);
        return $"{trees.Count:N0} arbres ajoutés au PropGrid et adaptés automatiquement à la hauteur du terrain.";
    }

    private static string TreeNumber(double value)
        => value.ToString("0.######", CultureInfo.InvariantCulture).Replace('.', ',');
}
