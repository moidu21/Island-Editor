using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using Anno1800IslandTool.Core.Models;
namespace Anno1800IslandTool.Core.Terrain;

/// <summary>Protect complete shared material-ID blocks intersecting river water, including vanilla curves.</summary>
public static class RiverMaterialProtection
{
    public static HashSet<int> ProtectedMaterialBlocks(IslandProject project, TerrainMaterialMap map)
    {
        var result = new HashSet<int>();
        if (map.Width <= 0 || map.Height <= 0 || string.IsNullOrWhiteSpace(project.RawContent.GameDataText)) return result;
        var doc = XDocument.Parse(project.RawContent.GameDataText);
        var size = Math.Max(1, project.Root.MapSize);
        foreach (var obj in doc.Descendants().Where(e => e.Element("River") != null))
        {
            var nodes = obj.Descendants("BezierCurve").Elements().ToArray();
            for (int i = 1; i < nodes.Length; i++)
            {
                var a = Vector(nodes[i-1].Element("p")); var b = Vector(nodes[i].Element("p"));
                var c = a + Vector(nodes[i-1].Element("o")); var d = b + Vector(nodes[i].Element("i"));
                var radius = Math.Max(Number(nodes[i-1].Element("w")), Number(nodes[i].Element("w"))) * .5f;
                if (radius <= 0) continue;
                var previous = a;
                for (int step = 1; step <= 16; step++)
                {
                    float t = step / 16f, u = 1-t;
                    var next = u*u*u*a + 3*u*u*t*c + 3*u*t*t*d + t*t*t*b;
                    // Include half a texel diagonal to protect intersecting pixels, not just their centers.
                    var r = radius + .5f * size * MathF.Sqrt(1f/((float)map.Width*map.Width)+1f/((float)map.Height*map.Height));
                    var min = Vector2.Min(previous,next)-new Vector2(r); var max = Vector2.Max(previous,next)+new Vector2(r);
                    for (int y=Math.Max(0,(int)Math.Floor(min.Y/size*map.Height));y<=Math.Min(map.Height-1,(int)Math.Ceiling(max.Y/size*map.Height));y++)
                    for (int x=Math.Max(0,(int)Math.Floor(min.X/size*map.Width));x<=Math.Min(map.Width-1,(int)Math.Ceiling(max.X/size*map.Width));x++)
                    {
                        var p = new Vector2((x+.5f)/map.Width*size,(y+.5f)/map.Height*size);
                        var axis = next-previous;
                        var fraction = axis.LengthSquared()>0 ? Math.Clamp(Vector2.Dot(p-previous,axis)/axis.LengthSquared(),0,1) : 0;
                        if (Vector2.DistanceSquared(p,previous+axis*fraction)<=r*r) result.Add(map.GetMaterialIdBlockIndex(x,y));
                    }
                    previous=next;
                }
            }
        }
        return result;
    }
    private static float Number(XElement? e) => float.TryParse(e?.Value.Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out var n) && float.IsFinite(n) ? n : 0;
    private static Vector2 Vector(XElement? e)
    {
        var v=e?.Value.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        return v is {Length:>=3} ? new Vector2(Number(new XElement("v",v[0])),Number(new XElement("v",v[2]))) : Vector2.Zero;
    }
}
