using Anno1800IslandTool.Pipeline.Services;
using Xunit;
namespace Anno1800IslandTool.Tests;
public class NativeConversionBatchesTests
{
    [Fact]
    public void LongPathsStayWithinWindowsCommandLineBudget()
    {
        var paths=Enumerable.Range(0,500).Select(i=>new string('a',700)+i+".xml").ToArray();
        var batches=NativeConversionBatches.Create(paths);
        Assert.Equal(paths,batches.SelectMany(b=>b));
        Assert.All(batches,b=>{Assert.InRange(b.Length,1,96);Assert.True(b.Sum(p=>p.Length+3)<=24000);});
    }
    [Fact]
    public async Task WorkIsConcurrentBoundedAndProgressCountsFinishedFiles()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int running=0,peak=0,total=0;var progress=new List<int>();
        var batches=Enumerable.Range(0,6).Select(i=>new[]{i.ToString()}).ToArray();
        await NativeConversionBatches.RunAsync(batches,2,async(b,ct)=>{
            var active=Interlocked.Increment(ref running);
            if(active==2){peak=2;entered.TrySetResult();}
            Assert.InRange(active,1,2);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5),ct);
            Interlocked.Increment(ref total);Interlocked.Decrement(ref running);
        },n=>progress.Add(n),CancellationToken.None);
        Assert.Equal(2,peak);Assert.Equal(6,total);Assert.Equal(Enumerable.Range(1,6),progress);
    }
    [Fact]
    public async Task FailureCancelsAndWaitsForOtherWorkers()
    {
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);bool stopped=false;
        await Assert.ThrowsAsync<IOException>(()=>NativeConversionBatches.RunAsync(new[]{new[]{"fail"},new[]{"wait"}},2,async(b,ct)=>{
            if(b[0]=="fail"){await started.Task.WaitAsync(TimeSpan.FromSeconds(5));throw new IOException("conversion failed");}
            started.SetResult();try{await Task.Delay(Timeout.Infinite,ct);}finally{stopped=true;}
        },null,CancellationToken.None));
        Assert.True(stopped);
    }
    [Fact]
    public async Task PreCanceledRunDoesNotStartConverters()
    {
        using var cts=new CancellationTokenSource();cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>NativeConversionBatches.RunAsync(new[]{new[]{"x"}},4,(_,_)=>throw new Exception("must not start"),null,cts.Token));
    }
    [Fact]
    public void ConcurrencyRespectsCpuAndMemoryBudget()
    {
        const long gb=1024L*1024*1024;
        Assert.Equal(8,NativeConversionBatches.WorkerCount(32,32*gb,1024*1024));
        Assert.Equal(2,NativeConversionBatches.WorkerCount(32,2*gb,1024*1024));
        Assert.Equal(1,NativeConversionBatches.WorkerCount(32,8*gb,gb));
        Assert.Equal(1,NativeConversionBatches.WorkerCount(1,32*gb,1024));
    }
}
