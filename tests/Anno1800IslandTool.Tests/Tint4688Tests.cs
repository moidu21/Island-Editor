using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.Pipeline.Editing;
using Anno1800IslandTool.Core.Models;
using Anno1800IslandTool.Core.Common;
using System.Xml.Linq;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class Tint4688Tests
{
 [Fact] public void NativeNeutralBlockIsSixteenNeutralPixels()
 {
  var p=GlobalTintBlockCodec.Decode(1,1,new[]{-31728,31727,0,0});
  Assert.Equal(64,p.Length);
  for(int i=0;i<64;i+=4) {Assert.Equal(131,(ushort)p[i]/257);Assert.Equal(129,(ushort)p[i+1]/257);Assert.Equal(131,(ushort)p[i+2]/257);}
 }
 [Fact] public void UneditedBlocksRoundTripBitExactlyEvenWithSelectors()
 {
  int[] words={-31728,31727,-12345,23456,-31728,31727,0,0};
  var p=GlobalTintBlockCodec.Decode(2,1,words);
  Assert.Equal(words,GlobalTintBlockCodec.Encode(8,4,p,words));
  p[0]=0;
  var modified=GlobalTintBlockCodec.Encode(8,4,p,words);
  Assert.Equal(words.Skip(4),modified.Skip(4));Assert.False(words.Take(4).SequenceEqual(modified.Take(4)));
 }
 [Fact] public void MalformedPackedTextureIsRejected()
 {Assert.Throws<ArgumentException>(()=>GlobalTintBlockCodec.Decode(2,2,new[]{1,2,3,4}));}
 [Fact] public void ExportDoesNotRetilePackedTintWhenActiveGridIsFull()
 {
  var p=new IslandProject();p.Root.MapSize=8;p.Root.ActiveMapGrid=new Int2(1,1);p.Root.ActiveMapRect=new RectI(0,0,1,1);p.Root.ActiveMapGridBits.Add(1);
  p.RawContent.Rd3dText="<Content><TintMapLevels><BaseTextureData><width>1</width><height>1</height><map>-31728 31727 -12345 23456</map></BaseTextureData></TintMapLevels></Content>";
  var r=new ProjectTextRegenerator();
  for(int i=0;i<3;i++){r.RegenerateRd3dText(p);Assert.Equal("-31728 31727 -12345 23456",XDocument.Parse(p.RawContent.Rd3dText!).Descendants("BaseTextureData").Single().Element("map")!.Value);}
 }
}

