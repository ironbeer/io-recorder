using System.Diagnostics;
using System.Text.Json;

namespace IoRecorder.Core;

internal static class LucidCollector
{
    public static async Task RunAsync(RecordingOptions options,string output,Stopwatch clock,IProgress<string>? progress,CancellationToken token)
    {
        if(string.IsNullOrWhiteSpace(options.LucidExecutable)) return;
        using var p=new Process();
        p.StartInfo=new(options.LucidExecutable) { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true };
        if(!string.IsNullOrWhiteSpace(options.LucidInstance)) { p.StartInfo.ArgumentList.Add("--id"); p.StartInfo.ArgumentList.Add(options.LucidInstance); }
        foreach(var arg in new[]{"perf","--all","--seconds","1"}) p.StartInfo.ArgumentList.Add(arg);
        try
        {
            p.Start();
            using var writer=new StreamWriter(Path.Combine(output,"lucidlink.jsonl")) { AutoFlush=true };
            var gate=new SemaphoreSlim(1,1);
            async Task Pump(StreamReader reader,string stream)
            {
                try
                {
                    while(await reader.ReadLineAsync(token) is { } line)
                    {
                        await gate.WaitAsync(token);
                        try { await writer.WriteLineAsync(JsonSerializer.Serialize(new { timeUs=(long)(clock.Elapsed.TotalMilliseconds*1000),stream,text=line },JsonStore.Options)); }
                        finally { gate.Release(); }
                    }
                }
                catch(OperationCanceledException) { }
            }
            var pumps=Task.WhenAll(Pump(p.StandardOutput,"stdout"),Pump(p.StandardError,"stderr"));
            try { await p.WaitForExitAsync(token); }
            catch(OperationCanceledException) { if(!p.HasExited) p.Kill(true); }
            await pumps;
            if(!token.IsCancellationRequested && p.ExitCode!=0) progress?.Report($"LucidLink CLI終了コード: {p.ExitCode}。lucidlink.jsonlを確認してください。");
        }
        catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or IOException)
        { progress?.Report("LucidLink統計を開始できません: "+ex.Message); }
    }
}
