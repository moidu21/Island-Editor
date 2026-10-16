using System.Diagnostics;
using System.Globalization;
using Anno1800IslandTool.Core.Common;
var text=string.Concat(Enumerable.Repeat("-32768 2147483647 ",1000000));
int[] ReadOld(){var tokens=text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);var values=new int[tokens.Length];for(int i=0;i<values.Length;i++)values[i]=int.Parse(tokens[i],CultureInfo.InvariantCulture);return values;}
var sw=Stopwatch.StartNew();long before=GC.GetTotalAllocatedBytes(true);var old=ReadOld();Console.WriteLine($"old: {sw.ElapsedMilliseconds} ms, {(GC.GetTotalAllocatedBytes(true)-before)/1048576} MiB allocated");
sw.Restart();before=GC.GetTotalAllocatedBytes(true);if(!IntegerGridText.TryRead(text,2000000,out var current))throw new Exception("Invalid grid");Console.WriteLine($"new: {sw.ElapsedMilliseconds} ms, {(GC.GetTotalAllocatedBytes(true)-before)/1048576} MiB allocated; equal={old.SequenceEqual(current)}");
if(!old.SequenceEqual(current))throw new Exception("Mismatch");