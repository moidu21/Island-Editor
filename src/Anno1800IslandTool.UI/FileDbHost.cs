using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Anno1800IslandTool.UI;
internal static class FileDbHost
{
    // Runs the official managed tool with our bundled .NET runtime, in a separate
    // process. Users do not need to install the obsolete .NET 6 runtime.
    public static int Run(string[] args)
    {
        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush=true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush=true });
            var file=Path.GetFullPath(args[1]);var directory=Path.GetDirectoryName(file)!;
            var context=new AssemblyLoadContext("FileDBReader",false);
            context.Resolving+=(_,name)=>File.Exists(Path.Combine(directory,name.Name+".dll"))?context.LoadFromAssemblyPath(Path.Combine(directory,name.Name+".dll")):null;
            var entry=context.LoadFromAssemblyPath(file).EntryPoint ?? throw new InvalidDataException(Anno1800IslandTool.Core.Localization.TextCatalog.T("FileDBReader entry point missing."));
            var result=entry.Invoke(null,[args.Skip(2).ToArray()]);
            if(result is Task<int> intTask)return intTask.GetAwaiter().GetResult();
            if(result is Task task)task.GetAwaiter().GetResult();
            return result is int code?code:0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
