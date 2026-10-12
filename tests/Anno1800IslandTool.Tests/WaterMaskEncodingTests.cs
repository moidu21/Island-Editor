using System.Reflection;
using System.Xml.Linq;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.Pipeline.Editing;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class WaterMaskEncodingTests
{
 [Theory] [InlineData(384)] [InlineData(768)] [InlineData(2040)] [InlineData(33)]
 public void AsymmetricMaskPreservesCoordinatesAndZeroPadding(int width)
 {
  int height=width;
  var mask=new bool[width*height];
  for(int y=0;y<height;y++)for(int x=0;x<width;x++)mask[y*width+x]=(x*7+y*13)%101<3 || (x==width-1&&y%3==0);
  var data=WaterMaskEncoding.Pack(mask,width,height);
  Assert.Equal(((width+31)/32)*4*height,data.Length);
  Assert.Equal(mask,WaterMaskEncoding.Unpack(data,width,height));
  var native=(int[])typeof(ProjectTextRegenerator).GetMethod("PackBitsAlignedRows",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{mask,width,height})!;
  Assert.Equal(data,native.Select(x=>(byte)x));
  for(int y=0;y<height;y++)for(int x=width;x<WaterMaskEncoding.Stride(width)*8;x++)
   Assert.Equal(0,data[y*WaterMaskEncoding.Stride(width)+x/8]&(1<<(x%8)));
  var compact=new byte[(mask.Length+7)/8];
  for(int i=0;i<mask.Length;i++)if(mask[i])compact[i/8]|=(byte)(1<<(i%8));
  Assert.Equal(mask,WaterMaskEncoding.Unpack(compact,width,height));
  var doc=XDocument.Parse($"<Root><WorldManager><Water><x>{width}</x><y>{height}</y><bits>{string.Join(" ",compact)}</bits></Water></WorldManager></Root>");
  WaterMaskEncoding.Normalize(doc);
  var reload=XDocument.Parse(doc.ToString());
  var read=reload.Descendants("bits").Single().Value.Split(' ').Select(byte.Parse).ToArray();
  Assert.Equal(data,read);
  Assert.False(WaterMaskEncoding.Normalize(reload));
 }
 [Fact] public void RejectsUnexpectedLength()=>Assert.Throws<InvalidDataException>(()=>WaterMaskEncoding.Unpack(new byte[10],33,33));
}
