using System.Windows;
using Anno1800IslandTool.UI.Rendering;
class Probe : DirectX11Surface
{
 public int Frames;
 protected override void DrawSurface(SharpDX.Direct2D1.DeviceContext context) { Frames++; }
}
class Program
{
 [STAThread] static void Main()
 {
 var app=new Application();var probe=new Probe();
 var window=new Window{Width=200,Height=200,Left=-15000,Top=-15000,ShowInTaskbar=false,Content=probe};
 window.Loaded+=async (_,_)=>{
 try {
 await Task.Delay(600);
 if(probe.RenderingError!=null)throw new Exception(probe.RenderingError);
 if(probe.Frames<1)throw new Exception("Initial rendering missing");
 int count=probe.Frames;await Task.Delay(300);
 if(probe.Frames!=count)throw new Exception("Idle surface keeps rendering");
 for(int i=0;i<100;i++)probe.RequestRender();
 await Task.Delay(300);if(probe.Frames!=count+1)throw new Exception("Redraw requests not coalesced");
 probe.Visibility=Visibility.Collapsed;probe.RequestRender();await Task.Delay(150);
 if(probe.Frames!=count+1)throw new Exception("Hidden surface rendered");
 probe.Visibility=Visibility.Visible;await Task.Delay(300);
 if(probe.Frames!=count+2)throw new Exception("Visibility restore failed");
 Console.WriteLine("PASS: initial frame, idle, 100 requests coalesced, hide/show");
 }catch(Exception e){Console.WriteLine(e);Environment.ExitCode=1;}
 finally {window.Close();app.Shutdown();}
 };
 app.Run(window);
 }
}