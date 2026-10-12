namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>RD3D BaseTextureData dimensions count BC1 blocks, each stored as four Int16 words.</summary>
public static class GlobalTintBlockCodec
{
    private static void Validate(int width, int height, IReadOnlyList<int> words)
    {
        if(width<=0 || height<=0 || words.Count!=checked(width*height*4))
            throw new ArgumentException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Invalid BC1 base texture dimensions."));
    }
    private static void DecodeBlock(IReadOnlyList<int> words,int b,Span<int> pixels)
    {
        var c0=unchecked((ushort)words[b]);var c1=unchecked((ushort)words[b+1]);
        var lookup=(uint)(ushort)words[b+2]|((uint)(ushort)words[b+3]<<16);
        Span<int> palette=stackalloc int[12];
        for(var c=0;c<3;c++)
        {
            var shift=c==0?11:c==1?5:0;var mask=c==1?63:31;
            palette[c]=((c0>>shift)&mask)*255/mask;
            palette[3+c]=((c1>>shift)&mask)*255/mask;
            palette[6+c]=c0>c1?(2*palette[c]+palette[3+c])/3:(palette[c]+palette[3+c])/2;
            palette[9+c]=c0>c1?(palette[c]+2*palette[3+c])/3:0;
        }
        for(var i=0;i<16;i++)
        {
            var selector=(int)((lookup>>(i*2))&3);
            for(var c=0;c<3;c++)pixels[i*4+c]=unchecked((short)(palette[selector*3+c]*257));
            pixels[i*4+3]=selector==3 && c0<=c1?0:-1;
        }
    }
    public static int[] Decode(int blocksWide,int blocksHigh,IReadOnlyList<int> words)
    {
        Validate(blocksWide,blocksHigh,words);
        var width=checked(blocksWide*4);var pixels=new int[checked(width*blocksHigh*16)];
        Span<int> block=stackalloc int[64];
        for(var by=0;by<blocksHigh;by++)
        for(var bx=0;bx<blocksWide;bx++)
        {
            DecodeBlock(words,(by*blocksWide+bx)*4,block);
            for(var y=0;y<4;y++)block.Slice(y*16,16).CopyTo(pixels.AsSpan(((by*4+y)*width+bx*4)*4,16));
        }
        return pixels;
    }
    public static int[] Encode(int width,int height,IReadOnlyList<int> pixels,IReadOnlyList<int> originalWords)
    {
        if(width<=0 || height<=0 || width%4!=0 || height%4!=0 || pixels.Count!=checked(width*height*4))
            throw new ArgumentException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Invalid pixel dimensions."));
        Validate(width/4,height/4,originalWords);
        var result=originalWords.ToArray();
        void EncodeRow(int by)
        {
        Span<int> original=stackalloc int[64];Span<byte> bgra=stackalloc byte[64];Span<byte> encoded=stackalloc byte[8];
        for(var bx=0;bx<width/4;bx++)
        {
            var b=(by*(width/4)+bx)*4;DecodeBlock(originalWords,b,original);
            var changed=false;
            for(var y=0;y<4 && !changed;y++)
            for(var x=0;x<4 && !changed;x++)
            for(var c=0;c<3;c++)
                changed|=pixels[((by*4+y)*width+bx*4+x)*4+c]!=original[(y*4+x)*4+c];
            if(!changed)continue;
            for(var y=0;y<4;y++)
            for(var x=0;x<4;x++)
            {
                var i=((by*4+y)*width+bx*4+x)*4;var j=(y*4+x)*4;
                bgra[j]=(byte)((ushort)pixels[i+2]/257);bgra[j+1]=(byte)((ushort)pixels[i+1]/257);
                bgra[j+2]=(byte)((ushort)pixels[i]/257);bgra[j+3]=255;
            }
            DdsDxt1TextureConverter.EncodeDxt1Block(bgra,4,4,0,0,encoded);
            for(var c=0;c<4;c++)result[b+c]=System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(encoded.Slice(c*2,2));
        }
        }
        // Each row owns its scratch buffers and writes disjoint output blocks.
        if (width * (long)height >= 65536) Parallel.For(0, height / 4, EncodeRow);
        else for (var by = 0; by < height / 4; by++) EncodeRow(by);
        return result;
    }
}

