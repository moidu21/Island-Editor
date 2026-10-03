using System.IO;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using Buffer = System.Buffer;
using Anno1800IslandTool.Pipeline.Services;
namespace Anno1800IslandTool.UI.Rendering;

// A private device/context, used only on the preview worker. It never touches Helix's render context.
internal sealed class GamePreviewDdsReader : IDisposable
{
    private readonly Device device;
    private readonly ComputeShader shader;
    public GamePreviewDdsReader()
    {
        try { device = new Device(DriverType.Hardware, DeviceCreationFlags.None, FeatureLevel.Level_11_0); }
        catch (SharpDXException) { device = new Device(DriverType.Warp, DeviceCreationFlags.None, FeatureLevel.Level_11_0); }
        try
        {
            using var code = ShaderBytecode.Compile("Texture2D<float4> src:register(t0); RWTexture2D<float4> dst:register(u0); [numthreads(8,8,1)] void main(uint3 p:SV_DispatchThreadID){uint w,h;dst.GetDimensions(w,h);if(p.x<w && p.y<h)dst[p.xy]=src.Load(int3(p.xy,0));}","main","cs_5_0");
            shader = new ComputeShader(device,code);
        }
        catch { device.Dispose(); throw; }
    }
    public PreviewTexture Read(string path, float repetition, int maxSize=256)
    {
        using var file=File.OpenRead(path);using var reader=new BinaryReader(file);
        var header=reader.ReadBytes(128);
        if(header.Length!=128 || BitConverter.ToUInt32(header,0)!=0x20534444)throw new InvalidDataException("Invalid DDS: "+path);
        int h=BitConverter.ToInt32(header,12),w=BitConverter.ToInt32(header,16),mips=Math.Max(1,BitConverter.ToInt32(header,28));
        if(w<=0 || h<=0 || w>32768 || h>32768 || mips>16)throw new InvalidDataException("Invalid DDS dimensions.");
        uint four=BitConverter.ToUInt32(header,84);Format format;
        if(four==0x30315844)
        {
            var dx=reader.ReadBytes(20);if(dx.Length!=20)throw new InvalidDataException("Truncated DX10 header.");
            format=(Format)BitConverter.ToInt32(dx,0);
            if(BitConverter.ToInt32(dx,4)!=3 || BitConverter.ToInt32(dx,12)!=1)throw new InvalidDataException("DDS must be a single 2D texture.");
        }
        else format=four switch {0x31545844=>Format.BC1_UNorm,0x33545844=>Format.BC2_UNorm,0x35545844=>Format.BC3_UNorm,_=>Format.Unknown};
        // Keep encoded colour values; the preview blend uses the same colour convention throughout.
        format=format switch {Format.BC1_UNorm_SRgb=>Format.BC1_UNorm,Format.BC2_UNorm_SRgb=>Format.BC2_UNorm,Format.BC3_UNorm_SRgb=>Format.BC3_UNorm,Format.BC7_UNorm_SRgb=>Format.BC7_UNorm,_=>format};
        int block=format switch {Format.BC1_UNorm=>8,Format.BC2_UNorm or Format.BC3_UNorm or Format.BC7_UNorm=>16,_=>throw new InvalidDataException("Unsupported DDS format: "+format)};
        for(int mip=0;mip<mips-1 && Math.Max(w,h)>maxSize;mip++)
        {file.Position+=((w+3)/4)*((h+3)/4)*block;w=Math.Max(1,w/2);h=Math.Max(1,h/2);}
        if(Math.Max(w,h)>maxSize)throw new InvalidDataException("DDS has no small mip level: "+path);
        int pitch=((w+3)/4)*block,count=pitch*((h+3)/4);
        var data=reader.ReadBytes(count);if(data.Length!=count)throw new InvalidDataException("Truncated DDS mip.");
        var pin=GCHandle.Alloc(data,GCHandleType.Pinned);
        Texture2D source;
        try { source=new Texture2D(device,new Texture2DDescription{Width=w,Height=h,MipLevels=1,ArraySize=1,Format=format,SampleDescription=new(1,0),Usage=ResourceUsage.Immutable,BindFlags=BindFlags.ShaderResource},new DataRectangle(pin.AddrOfPinnedObject(),pitch)); }
        finally{pin.Free();}
        using(source)
        using(var srv=new ShaderResourceView(device,source))
        {
            var description=new Texture2DDescription{Width=w,Height=h,MipLevels=1,ArraySize=1,Format=Format.R8G8B8A8_UNorm,SampleDescription=new(1,0),Usage=ResourceUsage.Default,BindFlags=BindFlags.UnorderedAccess};
            using var target=new Texture2D(device,description);using var uav=new UnorderedAccessView(device,target);
            var ctx=device.ImmediateContext;
            ctx.ComputeShader.Set(shader);ctx.ComputeShader.SetShaderResource(0,srv);ctx.ComputeShader.SetUnorderedAccessView(0,uav);
            ctx.Dispatch((w+7)/8,(h+7)/8,1);
            ctx.ComputeShader.SetShaderResource(0,null);ctx.ComputeShader.SetUnorderedAccessView(0,null);
            description.Usage=ResourceUsage.Staging;description.BindFlags=BindFlags.None;description.CpuAccessFlags=CpuAccessFlags.Read;
            using var staging=new Texture2D(device,description);ctx.CopyResource(target,staging);
            var box=ctx.MapSubresource(staging,0,MapMode.Read,SharpDX.Direct3D11.MapFlags.None);
            var pixels=new byte[w*h*4];
            try{for(int y=0;y<h;y++)Marshal.Copy(box.DataPointer+y*box.RowPitch,pixels,y*w*4,w*4);}
            finally{ctx.UnmapSubresource(staging,0);}
            return new(w,h,pixels,repetition);
        }
    }
    public void Dispose(){shader.Dispose();device.ImmediateContext.ClearState();device.ImmediateContext.Dispose();device.Dispose();}
}

