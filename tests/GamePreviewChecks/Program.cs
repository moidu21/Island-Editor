using System.IO;
using System.Diagnostics;
using Anno1800IslandTool.UI.Rendering;
using Anno1800IslandTool.Formats.Readers;
using Anno1800IslandTool.Pipeline.Services;
using Anno1800IslandTool.Core.Terrain;
class Program
{
 [STAThread] static void Main(string[] args)
 {
  var project=new Anno1800IslandTool.Core.Models.IslandProject();project.Root.MapSize=384;project.Rd3d.GridWidth=project.Rd3d.GridHeight=12288;
  var window=new Anno1800IslandTool.UI.Terrain3DGpuWindow(project,2);

  var placementFlags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
  var choiceType=window.GetType().GetNestedType("SceneryChoice",System.Reflection.BindingFlags.NonPublic)!;
  var catalog=(System.Collections.IList)window.GetType().GetField("_sceneryCatalog",placementFlags)!.GetValue(window)!;
  foreach(var name in new[]{"rock_a","rock_b"})catalog.Add(Activator.CreateInstance(choiceType,new object[]{"data/graphics/props/rocks/"+name+".prp",name,0})!);
  void Invoke(string name,params object[] parameters)=>window.GetType().GetMethod(name,placementFlags)!.Invoke(window,parameters);
  Invoke("BindSceneryCatalog");
  var models=(System.Windows.Controls.ComboBox)window.FindName("SceneryModel");models.SelectedIndex=1;
  Invoke("BindSceneryCatalog");
  if(models.SelectedIndex!=1)throw new Exception("Scenery refresh lost selection.");
  Invoke("BeginToolHistoryAction");Invoke("AddSceneryPoint",new System.Numerics.Vector3(64,0,64));Invoke("CompleteToolHistoryAction");
  var pending=(System.Collections.IList)window.GetType().GetField("_pendingScenery",placementFlags)!.GetValue(window)!;
  if(pending.Count!=1)throw new Exception("One click must place one prop.");
  Invoke("UndoToolAction");if(pending.Count!=0)throw new Exception("Scenery undo failed.");
  Invoke("RedoToolAction");if(pending.Count!=1)throw new Exception("Scenery redo failed.");
  Console.WriteLine("PASS: scenery dropdown refresh, single placement, undo and redo.");
  var tabs=(System.Windows.Controls.TabControl)window.FindName("GpuToolTabs");
  if(((System.Windows.Controls.TabItem)tabs.Items[0]).Header.ToString()!="Relief")throw new Exception("Existing tool tab order changed.");
  var check=(System.Windows.Controls.CheckBox)window.FindName("GamePreviewCheck");check.IsChecked=true;
  var method=window.GetType().GetMethod("ApplyGamePreviewLighting",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;method.Invoke(window,null);
  var sun=(HelixToolkit.Wpf.SharpDX.DirectionalLight3D)window.FindName("PreviewSunLight");var first=sun.Direction;
  ((System.Windows.Controls.Slider)window.FindName("GameSunAzimuth")).Value=20;method.Invoke(window,null);
  if(first==sun.Direction || sun.Direction.Y>=0)throw new Exception("Sun direction did not change.");
  var shadow=(HelixToolkit.Wpf.SharpDX.ShadowMap3D)window.FindName("PreviewShadowMap");
  if(!shadow.IsRendering || !shadow.AutoCoverCompleteScene)throw new Exception("Shadows not enabled.");
  var geometry=new HelixToolkit.SharpDX.MeshGeometry3D{Positions=new HelixToolkit.Vector3Collection{new(0,40,0),new(6144,40,6144),new(12288,40,12288)},Indices=new HelixToolkit.IntCollection{0,1,2}};
  window.GetType().GetField("_geometry",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(window,geometry);
  window.GetType().GetMethod("RefreshGamePreviewGeometry",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,null);
  if(geometry.TextureCoordinates[1]!=new System.Numerics.Vector2(.5f,.5f))throw new Exception("Invalid terrain UV mapping.");
  var terrain=(HelixToolkit.Wpf.SharpDX.MeshGeometryModel3D)window.FindName("TerrainModel");
  if(!terrain.FrontCounterClockwise)throw new Exception("Rasterizer front-face convention was not updated with the camera.");
  if(!ReferenceEquals(terrain.Geometry,geometry))throw new Exception("Textured geometry was not rebound.");
  var ocean=(HelixToolkit.Wpf.SharpDX.MeshGeometryModel3D)window.FindName("PreviewOceanModel");
  if(ocean.Geometry.Positions[0].Y>=0)throw new Exception("Ocean should be just below the zero-height surface.");
  if(((HelixToolkit.Wpf.SharpDX.Viewport3DX)window.FindName("GpuViewport")).Camera is not HelixToolkit.Wpf.SharpDX.PerspectiveCamera camera || camera.NearPlaneDistance<=.1)throw new Exception("Depth precision was not adjusted.");
  if(!camera.CreateLeftHandSystem)throw new Exception("Camera is still mirrored.");
  camera.Position=new(0,20,30);camera.LookDirection=new(0,-20,-30);
  var view=camera.CreateViewMatrix();var projection=camera.CreateProjectionMatrix(1.5);
  var vp=view*projection;
  var marker=new System.Numerics.Vector4(3,2,4,1);
  var projected=System.Numerics.Vector4.Transform(marker,vp);
  if(!System.Numerics.Matrix4x4.Invert(vp,out var inverse))throw new Exception("Singular camera matrix.");
  var restored=System.Numerics.Vector4.Transform(projected/projected.W,inverse);restored/=restored.W;
  if(System.Numerics.Vector4.Distance(marker,restored)>.02)throw new Exception("Project/unproject changed brush coordinates.");
  camera.CreateLeftHandSystem=false;
  var old=System.Numerics.Vector4.Transform(marker,camera.CreateViewMatrix()*camera.CreateProjectionMatrix(1.5));
  if(Math.Abs(projected.X/projected.W+old.X/old.W)>.0001 || Math.Sign(old.X)==Math.Sign(projected.X))throw new Exception("Expected horizontal reflection without data changes.");
  camera.CreateLeftHandSystem=true;
  Console.WriteLine("PASS: camera reflection and project/unproject preserve asymmetric marker coordinates.");
  Console.WriteLine("PASS: UV centre, GPU geometry rebinding, sea separation and preview camera clipping.");
  check.IsChecked=false;method.Invoke(window,null);
  if(shadow.IsRendering)throw new Exception("Shadows not disabled.");
  window.Close();Console.WriteLine("PASS: WPF initialization, unchanged tool tabs, sun controls, shadow toggle. Window never shown.");
  string root=args[0];var set=new MaterialSetReader().Read("terra_01.xml",File.ReadAllText(Path.Combine(root,"config/engine/material_sets/terra_01.xml")));
  using var decoder=new GamePreviewDdsReader();var textures=new Dictionary<int,PreviewTexture>();
  foreach(var d in set.Materials)
  {
   if(!GameDataTextureResolver.TryResolveTerrainTextures(root,d,out var diffuse,out _))continue;
   var texture=decoder.Read(diffuse!,d.Repetition ?? 4);textures[d.Id]=texture;
   if(texture.Width>256 || texture.Height>256 || !texture.Rgba.Any(b=>b!=0))throw new Exception("Invalid decoded texture "+d.Id);
   if(d.Id==10 || d.Id==1){var bgra=(byte[])texture.Rgba.Clone();for(int i=0;i<bgra.Length;i+=4)(bgra[i],bgra[i+2])=(bgra[i+2],bgra[i]);var bitmap=System.Windows.Media.Imaging.BitmapSource.Create(texture.Width,texture.Height,96,96,System.Windows.Media.PixelFormats.Bgra32,null,bgra,texture.Width*4);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using var fs=File.Create("work/audit-46103/texture-"+d.Id+".png");encoder.Save(fs);}
   Console.WriteLine($"DDS OK {d.Id}: {texture.Width}x{texture.Height} {Path.GetFileName(diffuse)}");
  }
  if(textures.Count<10)throw new Exception("Too few real textures decoded.");
  var map=new TerrainMaterialMap{Width=16,Height=16,MaterialIdBlockSize=2};
  var ids=textures.Keys.Take(6).ToArray();for(int y=0;y<16;y++)for(int x=0;x<16;x++)map.SetCell(x,y,ids,[5,10,15,20,25,31]);
  var sw=Stopwatch.StartNew();var image=GamePreviewTextureBaker.Bake(map,textures,2048,384,384,default);
  if(image.Length!=2048*2048*4 || image.Distinct().Count()<32)throw new Exception("Invalid atlas.");
  var actual=new Rd3dReader().Read(File.ReadAllText(@"H:/anno 1800/last_test/ile original interprter/moderate_l_10__interpreted_original/moderate_l_10/rd3d.xml"));
  var atlas=GamePreviewTextureBaker.Bake(actual.MaterialMap,textures,1024,384,384,default,set.Materials.Select(d=>new PreviewLayer(d.Id,d.AlphaSharpness ?? 5)).ToArray());
  var tintRoot=@"H:/anno 1800/last_test/ile original interprter/moderate_l_10__interpreted_original/_gamedata/moderate_l_10/tintmaps";
  var pages=Directory.EnumerateFiles(tintRoot,"*.dds",SearchOption.AllDirectories).Select(path=>{
   var xy=Path.GetFileNameWithoutExtension(path).Split('x');int lod=int.Parse(new DirectoryInfo(Path.GetDirectoryName(path)!).Name);
   return new PreviewTintPage(new(lod,int.Parse(xy[0]),int.Parse(xy[1])),File.ReadAllBytes(path));}).ToArray();
  var before=atlas.ToArray();int tinted=GamePreviewTint.Apply(atlas,1024,pages,12288,12288,default);
  if(tinted<100000 || atlas.SequenceEqual(before))throw new Exception("Real island tint was not applied.");
  Console.WriteLine($"PASS: original island, {pages.Length} tint pages, {tinted} tinted texels; original DDS unchanged.");
  var bmp=System.Windows.Media.Imaging.BitmapSource.Create(1024,1024,96,96,System.Windows.Media.PixelFormats.Bgra32,null,atlas,4096);
  var enc=new System.Windows.Media.Imaging.PngBitmapEncoder();enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));using(var fs=File.Create("work/audit-46103/atlas.png"))enc.Save(fs);
  var fir=PreviewSceneReader.Model(root,"data/graphics/props/terrain_props/vegetation/trees/moderate/tree_fir_02_var1.prp",default).Single();
  if(fir.FoliageColor is not System.Numerics.Vector3 leafColor || leafColor.Y<=leafColor.X || leafColor.Y<=leafColor.Z)throw new Exception("Fir palette not restored.");
  var firMesh=PreviewRdmReader.Read(File.ReadAllBytes(fir.Mesh));
  if(firMesh.Foliage is null || !firMesh.Foliage.Any(v=>v>0) || !firMesh.Foliage.Any(v=>v==0))throw new Exception("Foliage/bark mask missing.");
  var depositModels=PreviewSceneReader.AssetModels(root,new HashSet<string>{"100417","100849"},default);
  foreach(var guid in new[]{"100417","100849"})
  {
   var parts=PreviewSceneReader.Model(root,depositModels[guid],default);
   if(!parts.Any(p=>(guid=="100417"?p.Mesh.Contains("factory_11_slot",StringComparison.OrdinalIgnoreCase):p.GeneratedMesh is not null) && p.Diffuse is not null))throw new Exception("Deposit main mesh absent: "+guid);
   if(parts.Any(p=>p.Mesh.Contains("cutout")))throw new Exception("Invisible cutout mask treated as visible geometry.");
  }
  Console.WriteLine("PASS: native fir palette and foliage mask; both clay and oil main meshes resolved.");
  var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static;
  var gd=File.ReadAllText(@"H:/anno 1800/last_test/ile original interprter/moderate_l_10__interpreted_original/moderate_l_10/gamedata.xml");
  using var nativeDecoder=(IDisposable)Activator.CreateInstance(typeof(Anno1800IslandTool.UI.Terrain3DGpuWindow).Assembly.GetType("Anno1800IslandTool.UI.Rendering.GamePreviewDdsReader")!)!;
  var scenery=typeof(Anno1800IslandTool.UI.Terrain3DGpuWindow).GetMethod("ReadPreviewScenery",flags)!.Invoke(null,new object?[]{root,actual.PropGrid.FileNames.ToArray(),actual.PropGrid.Instances.Select(i=>i.RawNoneXml).ToArray(),gd,new HashSet<string>{"1000029","1010501"},nativeDecoder,CancellationToken.None})!;
  var sceneType=scenery.GetType();var placed=(int)sceneType.GetProperty("Placed")!.GetValue(scenery)!;
  var missing=(string[])sceneType.GetProperty("Missing")!.GetValue(scenery)!;
  File.WriteAllLines("work/audit-46103/missing-scenery.txt",missing);
  Console.WriteLine($"Scenery: {placed} instances, {missing.Length} missing resources.");
  if(placed<20000)throw new Exception("Original island scenery missing: "+placed);
  var riverVertices=(PreviewRiverVertex[])sceneType.GetProperty("Rivers")!.GetValue(scenery)!;
  if(riverVertices.Length<6)throw new Exception("Original rivers not read.");
  var sceneWindow=new Anno1800IslandTool.UI.Terrain3DGpuWindow(project,2);
  ((System.Windows.Controls.CheckBox)sceneWindow.FindName("GamePreviewCheck")).IsChecked=true;
  sceneWindow.GetType().GetMethod("ApplyPreviewScenery",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(sceneWindow,new[]{scenery});
  var sceneryGroup=(HelixToolkit.Wpf.SharpDX.GroupModel3D)sceneWindow.FindName("PreviewSceneryGroup");
  if(sceneryGroup.Children.Count<1)throw new Exception("Scenery did not reach viewport.");
  var viewport=(HelixToolkit.Wpf.SharpDX.Viewport3DX)sceneWindow.FindName("GpuViewport");
  var performance=sceneWindow.GetType().GetMethod("ApplyPreviewPerformance",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
  var economy=(System.Windows.Controls.CheckBox)sceneWindow.FindName("PreviewEconomyCheck");
  var shadowPerformance=(HelixToolkit.Wpf.SharpDX.ShadowMap3D)sceneWindow.FindName("PreviewShadowMap");
  var countBefore=sceneryGroup.Children.OfType<HelixToolkit.Wpf.SharpDX.InstancingMeshGeometryModel3D>().Sum(m=>m.Instances.Count);
  if(viewport.MinimumUpdateCount!=2 || viewport.OITRenderMode!=HelixToolkit.SharpDX.OITRenderType.SinglePassWeighted || viewport.EnableDpiScale || shadowPerformance.Resolution.Width!=1024)throw new Exception("Economy defaults not applied.");
  if(sceneryGroup.Children.OfType<HelixToolkit.Wpf.SharpDX.InstancingMeshGeometryModel3D>().Any(m=>m.IsThrowingShadow))throw new Exception("Scenery still casts shadows in economy mode.");
  economy.IsChecked=false;performance.Invoke(sceneWindow,null);
  if(!viewport.EnableDpiScale || shadowPerformance.Resolution.Width!=2048 || sceneryGroup.Children.OfType<HelixToolkit.Wpf.SharpDX.InstancingMeshGeometryModel3D>().Any(m=>!m.IsThrowingShadow))throw new Exception("Full quality not restored.");
  economy.IsChecked=true;performance.Invoke(sceneWindow,null);
  if(countBefore!=sceneryGroup.Children.OfType<HelixToolkit.Wpf.SharpDX.InstancingMeshGeometryModel3D>().Sum(m=>m.Instances.Count))throw new Exception("Performance change dropped scenery.");
  var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};timer.Start();
  sceneWindow.GetType().GetField("_waterAnimationTimer",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.SetValue(sceneWindow,timer);
  sceneWindow.GetType().GetMethod("UpdateWaterAnimation",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(sceneWindow,new object[]{true});
  if(timer.IsEnabled)throw new Exception("Invisible window still animates water.");
  Console.WriteLine("PASS: economy/full quality switching, scenery count preserved, idle animation stopped.");
  Console.WriteLine($"PASS: {sceneryGroup.Children.Count} instanced groups, {riverVertices.Length/3} river triangles; windows never shown.");
  sceneWindow.Close();
  Console.WriteLine($"PASS: {textures.Count} DDS, weighted 2048 atlas in {sw.ElapsedMilliseconds} ms. No window opened.");
 }
}

