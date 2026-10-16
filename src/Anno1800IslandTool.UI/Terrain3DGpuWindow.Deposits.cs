using System.Numerics;
using System.Windows;
using Anno1800IslandTool.Core.Common;
using Anno1800IslandTool.Core.Terrain;
using Anno1800IslandTool.Pipeline.Services;

namespace Anno1800IslandTool.UI;
public partial class Terrain3DGpuWindow
{
    private readonly List<DepositPlacement> _pendingDeposits = new();
    private void AddDepositPoint(Vector3 hit, DepositKind kind)
    {
        try
        {
            double size = _project.Root.MapSize;
            double width = _project.Rd3d.GridWidth > 0 ? _project.Rd3d.GridWidth : size*32;
            double height = _project.Rd3d.GridHeight > 0 ? _project.Rd3d.GridHeight : size*32;
            var point = new Float3(MathF.Floor((float)(hit.X/width*size))+.5f,
                TerrainHeightEncoding.LegacyFineHeightToWorldHeight(SceneYToDecodedHeight(hit.Y)),
                MathF.Floor((float)(hit.Z/height*size))+.5f);
            if (!float.IsFinite(point.Y) || point.Y <= 0 || point.X < 0 || point.Z < 0 || point.X >= size || point.Z >= size)
                throw new InvalidOperationException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Placez le puits sur un terrain émergé dans les limites de l’île."));
            var occupied = _pendingDeposits.Select(p=>p.Position).Concat(_project.Root.SlotObjects
                .Where(s=>DepositSlotService.IsDepositGuid(s.ObjectGuid)).Select(s=>s.Position));
            if (occupied.Any(p=>MathF.Abs(p.X-point.X)<1 && MathF.Abs(p.Z-point.Z)<1))
                throw new InvalidOperationException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Un emplacement de ressource existe déjà à cet endroit."));
            _pendingDeposits.Add(new(kind,point));
            RefreshMinePreview();
        }
        catch(Exception ex) { StatusTextBlock.Text=ex.Message; }
    }
    private void RemoveLastDepositButton_Click(object sender,RoutedEventArgs e)
    {
        BeginToolHistoryAction();
        if(_pendingDeposits.Count>0)_pendingDeposits.RemoveAt(_pendingDeposits.Count-1);
        RefreshMinePreview();
        CompleteToolHistoryAction();
    }
    private void CommitPendingDeposits()
    {
        if(_pendingDeposits.Count==0)return;
        DepositSlotService.Add(_project,_pendingDeposits);
        _pendingDeposits.Clear();
        RefreshMinePreview();
    }
}
