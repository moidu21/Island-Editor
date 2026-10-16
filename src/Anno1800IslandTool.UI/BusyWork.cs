using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
namespace Anno1800IslandTool.UI;

/// <summary>Modal, animated progress for CPU work that does not access WPF controls.</summary>
internal static class BusyWork
{
    public static bool IsBusy { get; private set; }

    private static readonly AsyncLocal<object?> OperationContext = new();
    private static object? _operationToken;
    public static bool OwnsOperation => IsBusy && ReferenceEquals(OperationContext.Value, _operationToken) && !_workerRunning;
    private static bool _workerRunning;

    public static T Run<T>(Window owner, Func<T> calculate) => RunCore(owner, calculate, false);
    public static void Run(Window owner, Action calculate) => Run(owner, () => { calculate(); return true; });

    // Legacy controller callbacks also read WPF settings. Keep these on their
    // dispatcher; their nested CPU jobs reuse the same modal progress window.
    public static T RunOnUi<T>(Window owner, Func<T> apply) => RunCore(owner, apply, true);
    public static void RunOnUi(Window owner, Action apply) => RunOnUi(owner, () => { apply(); return true; });

    // Completes only after the complete async chain (writes, uploads, refreshes).
    // The modal loop pumps the dispatcher while the editor remains disabled.
    public static void RunOperation(Window owner, Func<Task> apply)
        => RunCore(owner, () => true, true, async () => { await apply(); return true; });

    private static T WaitForWorker<T>(Func<T> calculate)
    {
        _workerRunning = true;
        try { return WaitForTask(Task.Run(calculate)); }
        finally { _workerRunning = false; }
    }

    private static T WaitForTask<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }

    private static T RunCore<T>(Window owner, Func<T> calculate, bool onUi, Func<Task<T>>? completeAsync = null)
    {
        owner.Dispatcher.VerifyAccess();
        if (IsBusy)
        {
            if (!OwnsOperation)
                throw new InvalidOperationException(UiLocalizationManager.T("Un travail est déjà en cours."));
            return completeAsync is not null ? WaitForTask(completeAsync()) : onUi ? calculate() : WaitForWorker(calculate);
        }
        IsBusy = true;
        _operationToken = new object();
        var windows = Application.Current.Windows.Cast<Window>().ToArray();
        var states = windows.Select(w => (Window: w, Enabled: w.IsEnabled)).ToArray();
        var popup = new Window {
            Title = UiLocalizationManager.T("Travail en cours"), Width = 420,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        // Hidden GPU controllers are not suitable owners; use the visible active window.
        var visibleOwner = owner.IsVisible ? owner : windows.FirstOrDefault(w => w.IsActive && w.IsVisible)
            ?? windows.LastOrDefault(w => w.IsVisible);
        if (visibleOwner is not null) popup.Owner = visibleOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = UiLocalizationManager.T("Travail en cours"), FontSize = 20, Margin = new Thickness(0,0,0,12) });
        panel.Children.Add(new TextBlock { Text = UiLocalizationManager.T("Veuillez patienter. Les commandes seront disponibles à la fin du calcul."), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 8, Margin = new Thickness(0,20,0,4) });
        popup.Content = panel;
        bool finished = false, started = false;
        T result = default!;
        ExceptionDispatchInfo? error = null;
        CancelEventHandler preventClose = (_, e) => e.Cancel = true;
        popup.Closing += (_, e) => { if (!finished) e.Cancel = true; };
        popup.ContentRendered += async (_, _) => {
            if (started) return;
            started = true;
            try
            {
                if (onUi)
                {
                    var previousContext = OperationContext.Value;
                    OperationContext.Value = _operationToken;
                    try { result = completeAsync is not null ? await completeAsync() : calculate(); }
                    finally { OperationContext.Value = previousContext; }
                }
                else result = await Task.Run(calculate);
            }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
            finally
            {
                // Drain clicks/keys queued during synchronous UI work while the
                // editor is still disabled, instead of replaying them afterwards.
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                finished = true; popup.Close();
            }
        };
        try
        {
            foreach (var w in windows) { w.Closing += preventClose; w.IsEnabled = false; }
            popup.ShowDialog();
        }
        finally
        {
            foreach (var state in states) { state.Window.Closing -= preventClose; state.Window.IsEnabled = state.Enabled; }
            IsBusy = false;
            _operationToken = null;
        }
        error?.Throw();
        return result;
    }
}
