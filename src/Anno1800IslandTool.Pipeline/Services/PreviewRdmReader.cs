using System.Numerics;

namespace Anno1800IslandTool.Pipeline.Services;

public sealed record PreviewRdmPart(int Start, int Count, int Material);
public sealed record PreviewRdmMesh(Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs, int[] Indices, PreviewRdmPart[] Parts, float[]? Foliage=null);

// Read-only RDM mesh layout: pointer targets have count/stride prefixes at offset -8.
// Format reference: https://github.com/lukts30/rdm4 (rdm_data_main, vertex).
public static class PreviewRdmReader
{
    public static PreviewRdmMesh Read(byte[] bytes)
    {
        if(bytes.Length<80 || bytes[0]!='R' || bytes[1]!='D' || bytes[2]!='M' || bytes[3]!=1)
            throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Unsupported RDM header."));
        int U(int p){Check(p,4);uint v=BitConverter.ToUInt32(bytes,p);if(v>int.MaxValue)throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Invalid RDM pointer."));return (int)v;}
        void Check(int p,long n){if(p<0 || n<0 || p+n>bytes.Length)throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Truncated RDM data."));}
        (int Count,int Stride) Block(int p){int n=U(p-8),s=U(p-4);if(n<=0 || s<=0)throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Invalid RDM block."));Check(p,(long)n*s);return(n,s);}
        int meta=U(32);Block(meta);Check(meta,24);
        int fmt=U(U(meta+4)),vertices=U(meta+12),indices=U(meta+16),parts=U(meta+20);
        var f=Block(fmt);var v=Block(vertices);var ix=Block(indices);var mp=Block(parts);
        if(f.Stride!=16 || f.Count>32 || v.Count>2_000_000 || ix.Count>6_000_000 || ix.Stride is not (2 or 4) || mp.Stride<12)
            throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Unsupported RDM layout."));
        var pos=new Vector3[v.Count];var norm=new Vector3[v.Count];var uv=new Vector2[v.Count];
        var foliage=new float[v.Count];bool readFoliage=false;
        bool hasPos=false,hasUv=false;int offset=0;
        for(int a=0;a<f.Count;a++)
        {
            int at=fmt+a*16,id=U(at),kind=U(at+4),count=U(at+12);
            int width=kind switch{5=>4,6=>2*count,7=>4*count,_=>throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Unsupported RDM vertex type."))};
            if(offset+width>v.Stride)throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("RDM vertex stride mismatch."));
            if(id is 0 or 1 or 4)
            {
                if(id==0)hasPos=true;if(id==4)hasUv=true;
                float Value(int start,int c)=>kind switch{5=>bytes[start+c]/255f*2-1,6=>(float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes,start+c*2)),_=>BitConverter.ToSingle(bytes,start+c*4)};
                if(kind!=5 && count<(id==4?2:3))throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Incomplete RDM attribute."));
                for(int i=0;i<v.Count;i++)
                {
                    int start=vertices+i*v.Stride+offset;
                    float x=Value(start,0),y=Value(start,1),z=id==4?0:Value(start,2);
                    if(!float.IsFinite(x)||!float.IsFinite(y)||!float.IsFinite(z))throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Non-finite RDM vertex."));
                    if(id==0)pos[i]=new(x,y,z);
                    else if(id==4)uv[i]=new(x,y);
                    else{var n=new Vector3(x,y,z);norm[i]=n.LengthSquared()>1e-8?Vector3.Normalize(n):Vector3.UnitY;}
                }
            }
            if(id==5 && kind==5 && !readFoliage)
            {
                for(int i=0;i<v.Count;i++)foliage[i]=bytes[vertices+i*v.Stride+offset]/255f;
                readFoliage=true;
            }
            offset+=width;
        }
        if(!hasPos || !hasUv)throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("RDM requires positions and UVs."));
        var triangles=new int[ix.Count];
        for(int i=0;i<ix.Count;i++){int n=ix.Stride==2?BitConverter.ToUInt16(bytes,indices+i*2):U(indices+i*4);if(n>=v.Count)throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Invalid RDM index."));triangles[i]=n;}
        var groups=new PreviewRdmPart[mp.Count];
        for(int i=0;i<mp.Count;i++){int p=parts+i*mp.Stride,start=U(p),count=U(p+4);if((long)start+count>ix.Count || count%3!=0)throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Invalid RDM material range."));groups[i]=new(start,count,U(p+8));}
        if(norm.All(n=>n==Vector3.Zero))
        {
            for(int i=0;i+2<triangles.Length;i+=3){int a=triangles[i],b=triangles[i+1],c=triangles[i+2];var n=Vector3.Cross(pos[b]-pos[a],pos[c]-pos[a]);norm[a]+=n;norm[b]+=n;norm[c]+=n;}
            for(int i=0;i<norm.Length;i++)norm[i]=norm[i].LengthSquared()>1e-8?Vector3.Normalize(norm[i]):Vector3.UnitY;
        }
        return new(pos,norm,uv,triangles,groups,readFoliage?foliage:null);
    }
}
