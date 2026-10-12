namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>Bounded independent FileDB processes; never share mutable interpreter state.</summary>
public static class NativeConversionBatches
{
    public static IReadOnlyList<string[]> Create(IEnumerable<string> paths)
    {
        var result=new List<string[]>(); var batch=new List<string>(); int chars=0;
        foreach(var path in paths)
        {
            // Leave room for executable, schema, quoting and options below Windows' 32767 limit.
            int cost=path.Length+3;
            if(cost>24000)throw new PathTooLongException(path);
            if(batch.Count>0 && (batch.Count>=96 || chars+cost>24000))
            {result.Add(batch.ToArray());batch.Clear();chars=0;}
            batch.Add(path);chars+=cost;
        }
        if(batch.Count>0)result.Add(batch.ToArray());
        return result;
    }

    public static int WorkerCount(int processors,long availableMemory,long largestInput)
    {
        // XML DOM, interpreted strings and binary buffers coexist in each child.
        long perWorker=Math.Max(512L*1024*1024,largestInput>long.MaxValue/24?long.MaxValue:largestInput*24);
        long budget=Math.Max(1,availableMemory/2);
        return (int)Math.Max(1,Math.Min(Math.Clamp(processors/2,1,8),budget/perWorker));
    }

    public static async Task RunAsync(IReadOnlyList<string[]> batches,int workers,
        Func<string[],CancellationToken,Task> convert,Action<int>? completed,CancellationToken token)
    {
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(token);
        int count=0;var progressLock=new object();
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure=null;
        try
        {
        await Parallel.ForEachAsync(batches,new ParallelOptions {MaxDegreeOfParallelism=Math.Max(1,workers),CancellationToken=stop.Token},async(batch,ct)=>
        {
            try
            {
                await convert(batch,ct);
                lock(progressLock){count+=batch.Length;completed?.Invoke(count);}
            }
            catch(Exception ex)
            {
                Interlocked.CompareExchange(ref failure,System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex),null);
                stop.Cancel();throw;
            }
        });
        }
        catch
        {
            token.ThrowIfCancellationRequested();
            failure?.Throw();
            throw;
        }
    }
}
