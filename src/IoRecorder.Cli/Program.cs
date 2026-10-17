using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using IoRecorder.Core;

Console.OutputEncoding=Encoding.UTF8;
try { return await Cli.Run(args); }
catch(Exception ex) { Console.Error.WriteLine("エラー: "+ex.Message); return 1; }
finally
{
    // Closed parts of a record or replay results are compressed in the background; finish them before the process ends.
    if(LogCompressor.Pending>0) { Console.WriteLine("記録を圧縮しています…"); await LogCompressor.WhenIdleAsync(); }
}

internal static class Cli
{
    public static async Task<int> Run(string[] args)
    {
        if(args.Length==0 || args[0] is "help" or "--help" or "-h") { Help(); return 0; }
        var a=new Arguments(args.Skip(1).ToArray());
        using var stop=new CancellationTokenSource();
        Console.CancelKeyPress+=(s,e)=>{ e.Cancel=true;stop.Cancel(); };
        var progress=new Progress<string>(Console.WriteLine);
        switch(args[0])
        {
            case "record":
            {
                var roots=a.Many("root"); var devices=a.Many("root-device");
                if(devices.Length>0 && devices.Length!=roots.Length) throw new ArgumentException("--root-device は --root と同じ数だけ、同じ順に指定してください。");
                var options=new RecordingOptions
                {
                    Mode=Enum.Parse<CaptureMode>(a.Get("mode","Hybrid"),true),
                    ProcessNames=a.Many("process").Length>0?a.Many("process"):[],
                    ProcessIds=a.Many("pid").Select(int.Parse).ToArray(),Roots=roots,
                    ResolvedRoots=roots.Select((r,i)=>RootResolver.Resolve(r,devices.Length>0?devices[i]:null)).ToArray(),
                    IncludeChildren=!a.Flag("no-children"),ProbeSizes=!a.Flag("no-size-probe"),OpenFilesAtStart=!a.Flag("no-open-files"),CollectDisk=!a.Flag("no-disk"),
                    LucidExecutable=a.Optional("lucid"),LucidInstance=a.Optional("lucid-id")
                };
                int duration=a.Int("duration",0);
                // Without --out: Documents\IORecorder\Recordings\yyyyMMdd-HHmmss_<label or what is measured>.
                string subject=a.Optional("label") ?? string.Join("・",options.ProcessNames.Select(Path.GetFileNameWithoutExtension).Concat(options.ProcessIds.Select(p=>"PID"+p)).DefaultIfEmpty(string.Join("・",roots.Select(r=>Path.GetFileName(r.TrimEnd('\\'))))));
                string output=Path.GetFullPath(a.Optional("out") ?? AppPaths.NewFolder(AppPaths.Recordings,subject));
                var recorder=new EtwRecorder();
                await recorder.RecordAsync(output,options,a.Get("label",subject.Length>0?subject:"I/O capture"),stop.Token,progress,()=>
                { if(duration>0) stop.CancelAfter(TimeSpan.FromSeconds(duration)); if(a.Optional("ready") is { } path) File.WriteAllText(path,"ready"); });
                Console.WriteLine("保存先: "+output); return 0;
            }
            case "generate":
            {
                // A benchmark plan also fixes the dummy contents with its own seed unless --seed is given.
                string session=a.Required("session"),planFile=Path.Combine(session,BenchmarkPlanner.PlanFile);
                string seedText=a.Optional("seed") is { } given?Seeds.Normalize(given) ?? throw new ArgumentException("--seed には文字か数字を含めてください。")
                    :File.Exists(planFile)?JsonStore.Read<BenchmarkPlan>(planFile).Config.Seed:"42";
                int seed=Seeds.Value(seedText);
                if(TestLocation.IndexWarning(a.Required("target")) is { } indexed) Console.WriteLine("注意: "+indexed);
                if(File.Exists(planFile)) BenchmarkPlanner.CheckSpace(JsonStore.Read<BenchmarkPlan>(planFile),a.Required("target"),includeDummy:true);
                // The same --process-only / --root-only as the replay: only the files that replay touches are made.
                var m=await DatasetBuilder.GenerateAsync(session,a.Required("target"),seed,progress,stop.Token,processOnly:a.Flag("process-only"),rootOnly:a.Flag("root-only"));
                if(m.ExcludedPaths>0) Console.WriteLine($"対象外 {m.ExcludedPaths:N0} パス（変換・作成できない名前。再生ではスキップします）。例と理由は {DatasetBuilder.Marker} の notes にあります。");
                return 0;
            }
            case "reset":
            {
                var s=await DatasetBuilder.ResetAsync(a.Required("target"),progress,stop.Token);
                Console.WriteLine($"削除 {s.DeletedFiles:N0} ファイル・{s.DeletedDirectories:N0} フォルダ / 作り直し {s.RecreatedFiles:N0} / サイズ復元 {s.ResizedFiles:N0} / 書き戻し {s.RewrittenBytes/1048576.0:N1} MiB"
                    +(s.SkippedPaths>0?$" / リンクを経由するなどで戻さなかったパス {s.SkippedPaths:N0}":""));
                return 0;
            }
            case "replay":
            {
                string session=a.Required("session"),target=a.Required("target"),planFile=Path.Combine(session,BenchmarkPlanner.PlanFile);
                // A benchmark plan carries the timing it was planned for and its thread count.
                var plan=File.Exists(planFile)?JsonStore.Read<BenchmarkPlan>(planFile):null;
                if(plan is not null) BenchmarkPlanner.CheckSpace(plan,target,includeDummy:false);
                var options=new ReplayOptions
                { Mode=Enum.Parse<ReplayMode>(a.Get("timing",(plan?.RecommendedTiming??ReplayMode.ThinkTime).ToString()),true),Speed=a.Double("speed",1),Workers=a.Int("workers",plan?.Config.Threads??8),ProcessOnly=a.Flag("process-only"),RootOnly=a.Flag("root-only"),
                  Paging=Enum.Parse<PagingReplay>(a.Get("paging","UnmatchedReads"),true),TrimLeadingIdle=a.Flag("trim-leading-idle") };
                // --record: the I/O on the test target is recorded by ETW during the replay (administrator), for `compare`.
                var result=a.Flag("record")?(await ReplayCapture.RunAsync(session,target,options,progress,stop.Token)).Summary:await new ReplayEngine().RunAsync(session,target,options,progress,stop.Token);
                Console.WriteLine(JsonSerializer.Serialize(result,JsonStore.Pretty));
                if(result.TraceFolder is { } trace) Console.WriteLine("再生の記録: "+Path.Combine(session,trace)+"（compare --session で元の記録と比べられます）");
                if(plan is not null) PrintBenchmark(BenchmarkReport.Write(session,result,Path.GetFullPath(target)),result);
                return result.Failed>0?2:result.Cancelled?130:0;
            }
            case "compare":
            {
                // The original record against the recording of one of its replays (the newest unless --trace is given).
                string session=Path.GetFullPath(a.Required("session"));
                string trace=a.Optional("trace") ?? ReplayCapture.List(session).Select(t=>t.Folder).FirstOrDefault() ?? throw new ArgumentException("この記録には、記録付きの再生がありません（replay --record で再生してください）。");
                // --scope replayed: only the requests the replay process sent, against the original within the replay's scope.
                var scope=a.Get("scope","all").ToLowerInvariant() switch { "all"=>ComparisonScope.All,"replayed"=>ComparisonScope.Replayed,_=>throw new ArgumentException("--scope は all か replayed です。") };
                var c=ReplayComparison.Build(session,Path.GetFullPath(trace),scope);
                Console.WriteLine($"元の記録: {session}\n再生の記録: {trace}（{c.Link.Options.Mode}、再生 {c.Link.ElapsedSeconds:N1} 秒）");
                Console.WriteLine(scope==ComparisonScope.Replayed?$"比べる範囲: 再生した要求だけ（再生したプロセス PID {c.ReplayProcessId}）\n":"比べる範囲: 全体（すべてのプロセス）\n");
                Console.WriteLine($"  {"項目",-22} {"元",14} {"再生",14} {"差",10}");
                foreach(var v in c.Values) Console.WriteLine($"  {v.Item,-22} {Value(v.Unit,v.Original),14} {Value(v.Unit,v.Replay),14} {Change(v.Original,v.Replay),10}");
                Console.WriteLine("\n  操作の件数（元 / 再生）: "+string.Join("、",c.Operations.Select(o=>$"{o.Operation} {o.Original:N0} / {o.Replay:N0}")));
                Console.WriteLine($"\n  プロセスごとの操作（元 / 再生、「{ReplayComparison.ReplayProcessSuffix}」が再生したプロセス）:");
                foreach(var p in c.Processes) Console.WriteLine($"    {p.Process,-32} {p.OriginalOperations,10:N0} / {p.ReplayOperations,10:N0}");
                if(c.IndexerNote is { } indexer) Console.WriteLine("\n  注意: "+indexer);
                return 0;
            }
            case "bench":
            {
                string configPath=a.Required("config");
                var config=BenchmarkPlanner.Load(configPath);
                if(a.Optional("seed") is { } seed) config.Seed=Seeds.Normalize(seed) ?? throw new ArgumentException("--seed には文字か数字を含めてください。");
                // Without --out: Documents\IORecorder\Benchmarks\yyyyMMdd-HHmmss_<config>_seed<N> (or _seed-<text>).
                string output=Path.GetFullPath(a.Optional("out") ?? AppPaths.NewFolder(AppPaths.Benchmarks,$"{Path.GetFileNameWithoutExtension(configPath)}_{Seeds.Tag(Seeds.Normalize(config.Seed)??"42")}"));
                var plan=BenchmarkPlanner.Create(config,output);
                Console.WriteLine($"""
                ベンチマークのI/O列を作成しました（レポートもこのフォルダに保存します）: {output}
                  seed {config.Seed} / {config.Threads} スレッド / {BenchmarkPlanner.Describe(config)}
                  既存ファイル {plan.Files:N0} 個 / {plan.InitialBytes/1073741824.0:N2} GiB（ダミー生成で作成）・実行中に新しく書くファイル {plan.NewFileBytes/1073741824.0:N2} GiB
                  操作 {plan.Operations:N0} 件 / Read {plan.PlannedReadBytes/1073741824.0:N2} GiB / Write {plan.PlannedWriteBytes/1073741824.0:N2} GiB
                次の手順:
                  iorecorder-cli generate --session "{output}" --target <試験先>\{Path.GetFileName(output)}
                  （LucidLinkならアップロード完了とキャッシュ条件を整えてから）
                  iorecorder-cli replay --session "{output}" --target <試験先>\{Path.GetFileName(output)}
                """);
                foreach(var (name,bytes) in plan.WorkloadBytes.OrderByDescending(p=>p.Value)) Console.WriteLine($"  {name,-24} {bytes/1073741824.0,8:N2} GiB  ({bytes*100.0/Math.Max(1,plan.PlannedReadBytes+plan.PlannedWriteBytes):N1}%)");
                return 0;
            }
            case "resolve-root":
                // Run unelevated: prints the device/UNC forms to pass to an elevated `record --root-device`.
                foreach(var root in a.Many("root")) Console.WriteLine(JsonSerializer.Serialize(RootResolver.Resolve(root),JsonStore.Pretty));
                return 0;
            case "analyze":
                var analysis=SessionAnalyzer.Export(a.Required("session"),progress);
                if(a.Flag("csv")) SessionAnalyzer.ExportCsv(a.Required("session"),progress);
                Console.WriteLine($"Read {analysis.Reads:N0} / Write {analysis.Writes:N0} / P95 {analysis.P95Ms:N2} ms"); return 0;
            case "demo":
                Console.WriteLine("合成データ: "+DemoSession.Create(a.Optional("out") ?? AppPaths.NewFolder(AppPaths.Demo,"demo"))); return 0;
            case "workload":
                await Workload(a.Required("root"),a.Int("seconds",4),stop.Token); return 0;
            case "verify-etw":
                return await Verify(a.Optional("out") ?? AppPaths.NewFolder(AppPaths.Verification,"verify-etw"),progress);
            default: throw new ArgumentException("不明なコマンドです: "+args[0]);
        }
    }
    private static string Value(ComparisonUnit unit,double? value)=>value is not { } v?"—":unit switch
    {
        ComparisonUnit.Seconds=>$"{v:N2} 秒",ComparisonUnit.Bytes=>v>=1L<<30?$"{v/1073741824:N2} GiB":$"{v/1048576:N1} MiB",ComparisonUnit.MiBps=>$"{v:N1} MiB/s",
        ComparisonUnit.Iops=>$"{v:N0} IOPS",ComparisonUnit.Ms=>$"{v:N2} ms",_=>$"{v:N0}"
    };
    private static string Change(double? original,double? replay)=>original is > 0 && replay is { } r?$"{(r/original.Value-1)*100:+0;-0;0}%":"—";
    private static void PrintBenchmark(List<BenchmarkWorkloadResult> rows,ReplaySummary summary)
    {
        Console.WriteLine($"\nベンチマーク結果  {summary.ElapsedSeconds:N1} 秒 / 合計 {rows.Sum(r=>r.MiBps):N1} MiB/s / {rows.Sum(r=>r.Iops):N0} IOPS / 失敗 {summary.Failed:N0}");
        Console.WriteLine($"  {"workload",-24} {"MiB/s",9} {"IOPS",9} {"P50 ms",8} {"P95 ms",8} {"P99 ms",8} {"失敗",6}");
        foreach(var r in rows) Console.WriteLine($"  {r.Name,-24} {r.MiBps,9:N1} {r.Iops,9:N0} {r.P50Ms,8:N2} {r.P95Ms,8:N2} {r.P99Ms,8:N2} {r.Failed,6:N0}");
        Console.WriteLine($"  レポート: {Path.GetFileNameWithoutExtension(summary.OutcomeFile)}-report.html（数値は -bench.json / -timeline.csv）");
    }
    private static async Task Workload(string root,int seconds,CancellationToken token)
    {
        Directory.CreateDirectory(root);
        string path=Path.Combine(root,"capture-test.bin");
        byte[] bytes=new byte[65536]; new Random(42).NextBytes(bytes);
        using(var file=new FileStream(path,FileMode.Create,FileAccess.ReadWrite,FileShare.ReadWrite|FileShare.Delete))
        {
            file.Write(bytes);file.Write(bytes);file.Flush(true);
            var until=DateTime.UtcNow.AddSeconds(seconds); int i=0;
            while(DateTime.UtcNow<until)
            {
                token.ThrowIfCancellationRequested(); file.Position=(i++%8)*4096;
                file.ReadExactly(bytes,0,4096); file.Position=65536; file.Write(bytes,0,16384); await Task.Delay(20,token);
            }
            file.SetLength(196608);file.SetLength(98304);file.Flush(true);
        }
        File.Move(path,Path.Combine(root,"renamed.bin"));
        File.WriteAllBytes(Path.Combine(root,"delete-me.tmp"),bytes); File.Delete(Path.Combine(root,"delete-me.tmp"));
        // An existence check that fails, then CREATE_NEW.
        string created=Path.Combine(root,"created-new.bin");
        if(!File.Exists(created)) using(var file=new FileStream(created,FileMode.CreateNew,FileAccess.Write)) file.Write(bytes);
        // A rename through the open handle (SetFileInformationByHandle / FileRenameInfo), then use the new name.
        string openSource=Path.Combine(root,"open-rename.bin"),openTarget=Path.Combine(root,"open-renamed.bin");
        File.WriteAllBytes(openSource,bytes);
        using(var handle=CreateFileW(openSource,0x80000000|0x40000000|0x00010000,7,0,3,0,0))
        {
            if(handle.IsInvalid) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            var name=Encoding.Unicode.GetBytes(openTarget); var info=new byte[20+name.Length+2];
            BitConverter.GetBytes(name.Length).CopyTo(info,16); name.CopyTo(info,20);
            if(!SetFileInformationByHandle(handle,3,info,(uint)info.Length)) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            RandomAccess.Read(handle,bytes.AsSpan(0,4096),0);
        }
        using(var file=new FileStream(openTarget,FileMode.Open,FileAccess.Read,FileShare.ReadWrite,1)) file.ReadExactly(bytes,0,4096);
        // Data written without buffering is not cached, so the cached reads below must fill the cache with paging reads.
        string uncached=Path.Combine(root,"paging-probe.bin");
        {
            const int length=1024*1024; var pinned=GC.AllocateUninitializedArray<byte>(length+4096,pinned:true);
            int shift=(int)((4096-(System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(pinned,0).ToInt64()&4095))&4095);
            var aligned=pinned.AsSpan(shift,length); new Random(7).NextBytes(aligned);
            using var handle=File.OpenHandle(uncached,FileMode.Create,FileAccess.ReadWrite,FileShare.Read,(FileOptions)0x20000000|FileOptions.WriteThrough);
            RandomAccess.Write(handle,aligned,0);
        }
        using(var file=new FileStream(uncached,FileMode.Open,FileAccess.Read,FileShare.Read,1))
        { file.ReadExactly(bytes,0,65536); file.Position=524288; file.ReadExactly(bytes,0,65536); }
        // A directory opened with FILE_FLAG_BACKUP_SEMANTICS and no FILE_DIRECTORY_FILE.
        string directory=Path.Combine(root,"dir-probe"); Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory,"child.bin"),bytes.AsSpan(0,4096).ToArray());
        using(var handle=CreateFileW(directory,0x80000000,7,0,3,0x02000000,0)) { if(handle.IsInvalid) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()); }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path,uint access,uint share,nint security,uint mode,uint flags,nint template);
    [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle,int infoClass,byte[] info,uint size);
    private static async Task<int> Verify(string output,IProgress<string> progress)
    {
        if(!EtwRecorder.IsAdministrator) throw new UnauthorizedAccessException("verify-etwは管理者権限で実行してください。");
        output=Path.GetFullPath(output); Directory.CreateDirectory(output);
        string workload=Path.Combine(output,"workload"),session=Path.Combine(output,"session");
        using var stop=new CancellationTokenSource();
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder=new EtwRecorder();
        var record=recorder.RecordAsync(session,new() { Mode=CaptureMode.Folder,Roots=[workload],ProcessNames=[] },"ETW integration verification",stop.Token,progress,()=>ready.TrySetResult());
        if(await Task.WhenAny(ready.Task,record)!=ready.Task) await record;
        await Task.Delay(500);
        var info=new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=false,CreateNoWindow=true };
        // dotnet-run uses the dotnet host; published and built apphosts use their own exe.
        if(string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"dotnet",StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(Cli).Assembly.Location);
        foreach(var arg in new[]{"workload","--root",workload,"--seconds","3"}) info.ArgumentList.Add(arg);
        using var p=Process.Start(info)!;
        await p.WaitForExitAsync(); await Task.Delay(2000); stop.Cancel();
        var result=await record;
        var events=RecordLog.Read<IoEvent>(session, RecordLog.Events).ToArray();
        var manifest=JsonStore.Read<SessionManifest>(Path.Combine(session,"session.json"));
        var checks=new List<object>();
        bool Check(string name,bool required,bool? ok,string detail)
        {
            checks.Add(new { name,required,status=ok is null?"UNAVAILABLE":ok.Value?"PASS":required?"FAIL":"INFO",detail });
            return !required || ok==true;
        }
        FileEntry? Entry(string name)=>result.FileManifest.FirstOrDefault(f=>f.Path.EndsWith("\\"+name,StringComparison.OrdinalIgnoreCase));
        bool basic=p.ExitCode==0 && result.Reads>0 && result.Writes>0 && events.Any(e=>e.Operation==IoOperation.SetEndOfFile && e.Size==98304) && events.Any(e=>e.Operation==IoOperation.Read && e.Size==4096 && e.EndUs.HasValue) && result.LatencySamples>0;
        bool passed=Check("basic-read-write-eof",true,basic,$"workload exit {p.ExitCode}, reads {result.Reads}, writes {result.Writes}, latency samples {result.LatencySamples}");
        passed&=Check("root-match",true,manifest.RootMatchedEvents>0,$"{manifest.RootMatchedEvents:N0} events matched the root; unresolved device prefixes: {string.Join(", ",manifest.UnresolvedDevicePrefixes.Keys)}");
        passed&=Check("etw-loss",true,manifest.EtwLossCountAvailable?manifest.EtwEventsLost==0:null,manifest.EtwLossCountAvailable?$"{manifest.EtwEventsLost:N0} events lost":"loss count not available");
        var created=Entry("created-new.bin");
        passed&=Check("created-after-failed-lookup",true,created is null?null:!created.InitiallyExists,created is null?"file not recorded":$"initiallyExists={created.InitiallyExists}, confidence={created.SizeConfidence}");
        var directory=Entry("dir-probe");
        passed&=Check("directory-without-directory-flag",true,directory is null?null:directory.IsDirectory,directory is null?"directory not recorded":$"isDirectory={directory.IsDirectory}");
        var renames=events.Where(e=>e.Operation==IoOperation.Rename).ToArray();
        Check("rename-destination",false,renames.Length==0?null:renames.Any(e=>e.DestinationPath?.EndsWith("open-renamed.bin",StringComparison.OrdinalIgnoreCase)==true),string.Join("; ",renames.Select(e=>Path.GetFileName(e.Path)+" -> "+(e.DestinationPath is null?"(unresolved)":Path.GetFileName(e.DestinationPath)))));
        var probe=events.Where(e=>e.Operation==IoOperation.Read && e.Path.EndsWith("\\paging-probe.bin",StringComparison.OrdinalIgnoreCase) && !e.Failed).ToArray();
        long appBytes=probe.Where(e=>!e.IsPagingIo).Sum(e=>e.Transferred??e.Size),pagingBytes=probe.Where(e=>e.IsPagingIo).Sum(e=>e.Transferred??e.Size);
        Check("paging-split",false,probe.Length==0?null:appBytes==131072 && probe.Any(e=>e.IsPagingIo),$"application read bytes {appBytes:N0} (expected 131,072), paging read bytes {pagingBytes:N0}, flags seen: {string.Join(",",probe.Select(e=>e.IoFlags is { } f?$"0x{f:X}":"-").Distinct())}");
        Check("unhandled-fileio-opcodes",false,manifest.UnhandledFileIoOpcodes.Count==0,string.Join(", ",manifest.UnhandledFileIoOpcodes.Select(o=>$"opcode {o.Key}: {o.Value}")));
        JsonStore.Write(Path.Combine(output,"verification.json"),new { passed,checks,result.Reads,result.Writes,result.PagingReads,result.LatencySamples,operations=result.Operations,eof=events.Where(e=>e.Operation==IoOperation.SetEndOfFile).Select(e=>e.Size).ToArray(),renameResolved=renames.Any(e=>e.DestinationPath!=null) });
        Console.WriteLine(passed?"ETW実機テスト PASS":"ETW実機テスト FAIL — verification.jsonを確認してください。"); return passed?0:2;
    }
    private static void Help()=>Console.WriteLine($"IO Recorder {ToolVersion.Text} — Windows ETW / Synthetic workload replay\n"+"""

record     --mode Process|Folder|Hybrid --process AfterFX --pid 1234 --root D:\Production
           [--duration 30] [--label "LucidLink warm"] [--out <フォルダ>]
           [--root-device \Device\HarddiskVolume9\Production]
           [--no-children] [--no-size-probe] [--no-open-files] [--no-disk] [--lucid lucid.exe] [--lucid-id ID]
resolve-root --root L:\Production  (通常権限で実行。昇格後に見えないドライブのデバイスパス・UNCを表示)
generate   --session <記録フォルダ> --target L:\Bench\20261017-153012_AfterFX [--seed 42]
           [--process-only] [--root-only]  (replay と同じ範囲を指定。その範囲が使うファイルだけ作る)
replay     --session <記録フォルダ> --target L:\Bench\20261017-153012_AfterFX
           [--timing ThinkTime|Timeline] [--speed 1] [--workers 8]
             ThinkTime（既定）: 完了を待ち、記録と同じ間隔で次を出す（作業時間の比較向け）
             Timeline: 記録の時刻どおりに出す（同じ負荷での遅延比較向け）
           [--process-only] [--root-only]
           [--paging UnmatchedReads|None|All] [--trim-leading-idle] [--record]
             --record: 再生中の試験先のI/OもETWで記録する（管理者権限）。compare で元の記録と比べる
compare    --session <記録フォルダ> [--trace <記録フォルダ>\replays\<日時>]  (元の記録と、その再生の記録を比べる。省略時は最新)
           [--scope all|replayed]  (replayed: 再生したプロセスの要求だけを、元の記録の再生対象と比べる。既定は all)
reset      --target L:\Bench\20261017-153012_AfterFX  (実行済みのダミーを生成直後の状態に戻す。書き換わった部分だけ書き戻す)
analyze    --session <記録フォルダ> [--csv]  (--csv で全イベントを events.csv にも書き出す)
bench      --config <構成ファイル> [--seed 42 または --seed "LucidLink試験A"] [--out <フォルダ>]
           構成ファイルとseedから決まったI/O列を作成。続けて generate / replay で実行
demo       [--out <フォルダ>]
verify-etw [--out <フォルダ>]  (管理者権限・実I/Oによる自動テスト)

--out を省略すると、すべて ドキュメント\IORecorder の下に保存します:
  Recordings\  Benchmarks\  Configs\（構成ファイル）  Verification\  Demo\
generate の --target は、試験先に置くダミーのフォルダそのもの（新規または空）です。

process/root/pid は複数回指定できます。Hybridはプロセス一致 OR フォルダ一致です。
recordのみ管理者権限が必要です。Ctrl+Cで停止して確定します。
原本の内容は記録しません。再生先は本ツールが生成した隔離データセットだけです。
""");
}

internal sealed class Arguments
{
    private readonly Dictionary<string,List<string>> _values=new(StringComparer.OrdinalIgnoreCase);
    public Arguments(string[] args)
    {
        for(int i=0;i<args.Length;i++)
        {
            if(!args[i].StartsWith("--")) throw new ArgumentException("引数の形式が不正です: "+args[i]);
            string key=args[i][2..],value=i+1<args.Length&&!args[i+1].StartsWith("--")?args[++i]:"true";
            if(!_values.TryGetValue(key,out var list)) _values[key]=list=[];list.Add(value);
        }
    }
    public string[] Many(string key)=>_values.GetValueOrDefault(key)?.ToArray()??[];
    public string? Optional(string key)=>_values.TryGetValue(key,out var v)?v[^1]:null;
    public string Get(string key,string fallback)=>Optional(key)??fallback;
    public string Required(string key)=>Optional(key)??throw new ArgumentException("--"+key+" を指定してください。");
    public bool Flag(string key)=>Optional(key)=="true";
    public int Int(string key,int fallback)=>int.Parse(Get(key,fallback.ToString()),CultureInfo.InvariantCulture);
    public double Double(string key,double fallback)=>double.Parse(Get(key,fallback.ToString(CultureInfo.InvariantCulture)),CultureInfo.InvariantCulture);
}
