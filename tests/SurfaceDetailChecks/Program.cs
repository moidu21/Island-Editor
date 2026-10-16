using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Anno1800IslandTool.UI;
using Anno1800IslandTool.Pipeline.Import;
using Anno1800IslandTool.Pipeline.Export;
using Anno1800IslandTool.Formats.Readers;
class Program {
 sealed class ExportLog : IProgress<string> { public readonly System.Collections.Concurrent.ConcurrentQueue<string> Lines=new(); public void Report(string line){Lines.Enqueue(line);Console.WriteLine(line);} }
 static IEnumerable<T> Find<T>(DependencyObject obj) where T:DependencyObject {if(obj is T t)yield return t;for(int i=0;i<VisualTreeHelper.GetChildrenCount(obj);i++)foreach(var item in Find<T>(VisualTreeHelper.GetChild(obj,i)))yield return item;}
 [STAThread] static int Main(string[] args) {
 var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
 string output=args[2];Directory.CreateDirectory(output);
 var project=new IslandImporter().Import(args[1]);
 var raw=File.ReadAllText(Path.Combine(args[0],"config/engine/material_sets/terra_01.xml"));
 var set=new MaterialSetReader().Read("terra_01.xml",raw);project.MaterialSet=set;project.RawContent.MaterialSetText=raw;
 var window=new SurfaceDetailWindow(project,set){ShowInTaskbar=false,Opacity=0};
 Exception? error=null;
 window.Loaded+=(_,_)=>{try {
 Find<CheckBox>(window).Single().IsChecked=true;
 Find<Button>(window).Single(b=>b.Content?.ToString()=="Générer l’aperçu").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
 if(Find<Image>(window).Single().Source is null)throw new Exception("No preview: "+string.Join(" | ",Find<TextBlock>(window).Select(t=>t.Text)));
 window.Opacity=1;window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render((Visual)window.Content);
 var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(output,"dialog.png")))encoder.Save(stream);
 Find<Button>(window).Single(b=>b.Content?.ToString()=="Appliquer au projet").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
 }catch(Exception e){error=e;window.Close();}};
 window.ShowDialog();if(error is not null)throw error;
 if(project.SurfaceDetails?.TintDetails?.Enabled!=true)throw new Exception("Apply failed");
 var exporter=new IslandExporter();var state=project.SurfaceDetails;project.SurfaceDetails=null;
 exporter.Export(project,Path.Combine(output,"baseline.zip"));project.SurfaceDetails=state;
 var applied=Anno1800IslandTool.Pipeline.Services.TerrainTintMapExportSynchronizer.ApplyToProject(project);
 if(applied==0)throw new Exception("No project tintmaps generated");
 var exportLog=new ExportLog();
 exporter.Export(project,Path.Combine(output,"enabled.zip"),new ExportOptions{Progress=exportLog});
 if(!exportLog.Lines.Any(s=>s.Contains("génération des tintmaps")) || !exportLog.Lines.Any(s=>s.Contains("fichiers")))throw new Exception("Missing export preparation progress");
 int changed=0;
 using(var baseline=ZipFile.OpenRead(Path.Combine(output,"baseline.zip")))using(var enabled=ZipFile.OpenRead(Path.Combine(output,"enabled.zip"))) {
 foreach(var entry in baseline.Entries) {
 if(entry.FullName.StartsWith("_anno1800islandtool/"))continue;
 var target=enabled.GetEntry(entry.FullName)??throw new Exception("Missing "+entry.FullName);
 using var a=entry.Open();using var b=target.Open();if(!SHA256.HashData(a).SequenceEqual(SHA256.HashData(b))) {
 if(entry.FullName.Contains("/tintmaps/")){changed++;continue;}
 using var xa=entry.Open();using var xb=target.Open();
 if(!entry.FullName.EndsWith(".xml") || !System.Xml.Linq.XNode.DeepEquals(System.Xml.Linq.XDocument.Load(xa),System.Xml.Linq.XDocument.Load(xb)))throw new Exception("Changed "+entry.FullName);
 }}}
 if(changed==0)throw new Exception("No tint changed");
 var reopened=new IslandImporter().Import(Path.Combine(output,"enabled.zip"));
 foreach(var page in project.TintMapPages.Values)
 if(!page.RawDdsBytes.SequenceEqual(reopened.TintMapPages[page.Key].RawDdsBytes))throw new Exception("Project/export tint mismatch: "+page.Key);
 if(reopened.SurfaceDetails?.TintDetails!=state.TintDetails)throw new Exception("Settings lost");
 if(reopened.MaterialSet!.Materials.Single(m=>m.Id==30).FileName!=set.Materials.Single(m=>m.Id==30).FileName)throw new Exception("Material changed");
 Console.WriteLine("PASS: preview, apply, native export/reimport; "+changed+" tintmaps changed; all other payloads preserved.");return 0;
 }}
