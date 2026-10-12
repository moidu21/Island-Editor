using System.Windows;
using System.Windows.Threading;
using Anno1800IslandTool.UI;
namespace Anno1800IslandTool.UI { public static class UiLocalizationManager { public static string T(string s)=>s; } }
class Program
{
 [STAThread] static void Main()
 {
  var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
  var owner=new Window { Left=-15000, Top=-15000, Width=100, Height=100, ShowInTaskbar=false, WindowStartupLocation=WindowStartupLocation.Manual };
  var disabled=new Window { IsEnabled=false };
  owner.Show();
  app.Dispatcher.BeginInvoke(new Action(()=>{
   try {
    int ticks=0, main=Environment.CurrentManagedThreadId; bool closeBlocked=false;
    var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(40) };
    timer.Tick+=(_,_)=>{ticks++; if(ticks==2) { owner.Close();closeBlocked=owner.IsVisible; } };
    timer.Start();
    int worker=BusyWork.Run(owner,()=>{Thread.Sleep(400);return Environment.CurrentManagedThreadId;});
    timer.Stop();
    if(ticks<2||worker==main||!closeBlocked||!owner.IsEnabled||disabled.IsEnabled||BusyWork.IsBusy)throw new Exception("Busy state/dispatcher/close restoration failed");
    try { BusyWork.Run<int>(owner,()=>throw new InvalidOperationException("expected"));throw new Exception("Exception lost"); }
    catch(InvalidOperationException e) when(e.Message=="expected") {}
    if(!owner.IsEnabled||BusyWork.IsBusy)throw new Exception("Failure restoration failed");
    if(BusyWork.Run(owner,()=>42)!=42)throw new Exception("Retry failed");
    int nestedTicks=0;
    timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(30) };
    timer.Tick+=(_,_)=>{
      nestedTicks++;
      if(owner.IsEnabled)throw new Exception("Owner enabled inside composed operation");
      if(Application.Current.Windows.Cast<Window>().Count(w=>w.Title=="Travail en cours")!=1)throw new Exception("Nested popup duplicated");
      try { BusyWork.Run(owner,()=>0);throw new Exception("Reentrant operation accepted"); }
      catch(InvalidOperationException) {}
    };
    int composed=BusyWork.RunOnUi(owner,()=>{
      if(Environment.CurrentManagedThreadId!=main)throw new Exception("UI callback moved to worker");
      timer.Start();
      var value=BusyWork.Run(owner,()=>{Thread.Sleep(200);return 41;});
      timer.Stop();
      return BusyWork.RunOnUi(owner,()=>value+1);
    });
    if(composed!=42||nestedTicks<2||!owner.IsEnabled||BusyWork.IsBusy)throw new Exception("Composed work failed");
    try { BusyWork.RunOnUi<int>(owner,()=>BusyWork.Run<int>(owner,()=>throw new InvalidOperationException("nested expected"))); }
    catch(InvalidOperationException e) when(e.Message=="nested expected") {}
    if(!owner.IsEnabled||disabled.IsEnabled||BusyWork.IsBusy)throw new Exception("Nested failure restoration failed");
    int asyncTicks=0, phase=0;
    var asyncTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(20) };
    asyncTimer.Tick+=(_,_)=>{
      asyncTicks++;
      if(owner.IsEnabled || !BusyWork.IsBusy) throw new Exception("Popup ended before async completion");
      try { BusyWork.Run(owner,()=>0); throw new Exception("Async reentry accepted"); }
      catch(InvalidOperationException) {}
    };
    asyncTimer.Start();
    BusyWork.RunOperation(owner,async()=>{
      await Task.Delay(100);
      if(!BusyWork.OwnsOperation)throw new Exception("Async ownership lost");
      phase=BusyWork.Run(owner,()=>{Thread.Sleep(100);return 1;});
      await Task.Delay(100);
      BusyWork.RunOnUi(owner,()=>phase=2);
      asyncTimer.Stop();
    });
    if(phase!=2 || asyncTicks<3 || !owner.IsEnabled || BusyWork.IsBusy)throw new Exception("Async chain incomplete");
    try { BusyWork.RunOperation(owner,async()=>{await Task.Delay(20);throw new InvalidOperationException("async expected");}); }
    catch(InvalidOperationException e) when(e.Message=="async expected") {}
    if(!owner.IsEnabled || disabled.IsEnabled || BusyWork.IsBusy)throw new Exception("Async failure restoration failed");
    Console.WriteLine("PASS: async write/refresh chain awaited, reentry rejected during await, async failure recovered.");
    Console.WriteLine("PASS: composed UI/worker operations, single popup, reentrancy blocked, nested failure recovery.");
    Console.WriteLine($"PASS: {ticks} UI heartbeats during work, separate worker, close blocked, disabled state preserved, error propagated, retry successful.");
   } catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}
   finally{owner.Close();disabled.Close();app.Shutdown();}
  }));
  app.Run();
 }
}
