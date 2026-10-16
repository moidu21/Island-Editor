using System.Diagnostics;
using Anno1800IslandTool.Core.Localization;

namespace Anno1800IslandTool.Pipeline.Export;

/// <summary>Real stages and periodic elapsed-time reports, without estimated percentages.</summary>
internal sealed class ExportPreparationProgress : IDisposable
{
    private readonly IProgress<string>? sink;
    private readonly Timer? timer;
    private readonly object gate=new();
    private readonly Stopwatch elapsed=new();
    private string stage="";
    private bool disposed;
    public ExportPreparationProgress(IProgress<string>? sink)
    {
        this.sink=sink;
        if(sink is not null)timer=new Timer(_=>Tick(),null,10000,10000);
    }
    public void Stage(string name)
    {
        if(sink is null)return;
        lock(gate){if(disposed)return;stage=TextCatalog.T(name);elapsed.Restart();sink.Report(stage);}
    }
    public void Count(int done,int total)
    {
        if(sink is null)return;
        lock(gate){if(!disposed)sink.Report(string.Format(TextCatalog.T("{0} — {1}/{2} fichiers"),stage,done,total));}
    }
    private void Tick()
    {
        lock(gate)
        {
            if(disposed || stage.Length==0 || elapsed.Elapsed.TotalSeconds<10)return;
            sink?.Report(string.Format(TextCatalog.T("{0} — toujours en cours, {1} s pour cette étape"),stage,(int)elapsed.Elapsed.TotalSeconds));
        }
    }
    public void Dispose(){lock(gate){disposed=true;timer?.Dispose();}}
}
