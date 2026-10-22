using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace IoRecorder.Core;

public sealed class ReplayEngine
{
    // The "previous request" of a thread that has none yet: finished at the start of the replay clock.
    private static readonly Task<double> NotStarted=Task.FromResult(0.0);
    private const int LookAhead=1024;
    private static string[] Names(IoEvent e)=>string.IsNullOrEmpty(e.Path)?[]:e.Operation==IoOperation.Rename && !string.IsNullOrEmpty(e.DestinationPath)?[e.Path,e.DestinationPath]:[e.Path];
    // Creating, replacing, renaming or deleting a name (what another thread's later open of that name depends on).
    private static bool ChangesName(IoEvent e)=>!e.Failed && e.Operation switch
    {
        IoOperation.Open=>e.CreateDisposition is 0 or 2 or 4 or 5 || e.ExtraInfo==2,
        IoOperation.Rename or IoOperation.Delete=>true,
        _=>false
    };
    public async Task<ReplaySummary> RunAsync(string session,string dataset,ReplayOptions options,IProgress<string>? progress=null,CancellationToken token=default,IProgress<WorkProgress>? work=null)
    {
        if(options.Speed<=0 || options.Speed>1000 || double.IsNaN(options.Speed) || options.Workers<1 || options.Workers>256)
            throw new ArgumentException("再生速度・並列数が範囲外です。");
        dataset=Path.GetFullPath(dataset); PathPolicy.RejectReparsePoint(dataset);
        var settled=options.WaitForWriteBack?await WriteBack.SettleAsync(progress,token):null;
        var marker=Path.Combine(dataset,DatasetBuilder.Marker);
        var m=JsonStore.Read<DatasetManifest>(marker);
        var original=JsonStore.Read<SessionManifest>(Path.Combine(session,"session.json"));
        if(m.SessionId!=original.Id || m.SchemaVersion!=1) throw new InvalidDataException("このデータセットと記録の組み合わせが一致しません。");
        if(m.State!="Ready") throw new InvalidOperationException(m.State is "Replaying" or "Consumed"
            ?"このダミーは実行済みです。同じ初期状態で試験するため、「ダミーを初期状態に戻す」か、ダミーを生成し直してください。"
            :"このダミーは生成が完了していません。ダミーを生成し直してください。");
        m.Paths=new(m.Paths,StringComparer.OrdinalIgnoreCase);
        // The dummy holds only the files of the folders it was made for; the replay replays only the I/O inside them.
        options.Locations=[.. m.Locations]; var locations=LocationFilter.Of(m.Locations);
        // A path that would leave the dummy or pass through a junction / symbolic link inside it is skipped, not the whole run.
        var unsafePaths=PathPolicy.UnsafeInside(dataset,m.Paths.Values.Concat(m.Directories).Concat(m.InitialSizes.Keys));
        var result=new ReplaySummary { SessionId=m.SessionId,Options=options,Tool=ToolVersion.Text };
        if(settled is { Seconds: >= 1 } s)
            result.Notes.Add($"再生の前に、OSのディスクへの書き出しが落ち着くのを {s.Seconds:N0} 秒待ちました（未書き出し {s.StartBytes/1048576.0:N0} MiB → {s.EndBytes/1048576.0:N0} MiB）。");
        if(TestLocation.IndexWarning(dataset) is { } indexed) result.Notes.Add(indexed);
        if(unsafePaths.Count>0)
            result.Notes.Add($"ダミーのフォルダの外を指す、またはリンクを経由するため、{unsafePaths.Count:N0} パスへの操作を対象外にしました（SkippedUnsafePath）。例: "+string.Join(" / ",unsafePaths.Take(5)));
        if(!locations.IsEmpty) result.Notes.Add("場所を絞り込んで作ったダミーです。この中のI/Oだけを再生しました: "+locations.Text);
        if(m.ExcludedPaths>0) result.Notes.Add($"ダミー生成時に {m.ExcludedPaths:N0} パスを対象外にしています（変換できない名前など）。これらへの操作は SkippedUnresolvedPath です。");
        if(original.State=="SyntheticBenchmark") result.Notes.Add("合成ベンチマークの実行です（構成ファイルとseedから作ったI/O列）。");
        result.Notes.Add("スレッド内と同じファイルオブジェクトの順序を維持し、並列数に上限を設けます。OSスケジューラによる投入時刻の差を記録します。");
        result.Notes.Add("未観測のアクセス権・NoBufferingフラグ・ロック・mmap・列挙結果は完全再現できません。再生は通常のバッファ付きI/Oです。");
        result.Notes.Add("属性の問い合わせとフォルダの一覧は、記録と同じ情報クラスで、元が使ったハンドルに対して1回ずつ出します。高速I/Oの拒否（すぐ同じ要求が出し直されたもの）は、出し直された方だけを再生します（SkippedFastIoRetry）。");
        result.Notes.Add("開くときのアクセス権は記録に無いため、そのハンドルで記録された操作から推定します: 書き込みがあれば読み書き、読み取りがあれば読み取り、どちらも無ければ属性だけ（ウイルス対策の開くときの検査の対象になりにくい開き方）。");
        var accessPlan=new Dictionary<string,FileAccess>();
        var paging=new PagingClassifier();
        long total=0;
        work?.Report(new("記録を読み込み中",0,0,"アクセス権とページングI/Oの判定を準備しています"));
        foreach(var e in RecordLog.Read<IoEvent>(session, RecordLog.Events))
        {
            if(token.IsCancellationRequested) break;
            paging.Observe(e);
            if(!locations.Includes(e.Path)) continue;
            if(e.IsFileIo && !(options.ProcessOnly&&!e.MatchesProcess) && !(options.RootOnly&&!e.MatchesRoot)) total++;
            if(e.ByOtherProcess) continue;
            if(!e.Failed && e.Operation is IoOperation.Write or IoOperation.SetEndOfFile or IoOperation.SetAllocation or IoOperation.Flush && !(e.IsPagingIo && options.Paging!=PagingReplay.All))
                accessPlan[ReplayExecutor.AccessKey(e)]=FileAccess.ReadWrite;
            else if(!e.Failed && e.Operation==IoOperation.Read) accessPlan.TryAdd(ReplayExecutor.AccessKey(e),FileAccess.Read);
        }
        paging.Seal();
        if(!paging.FlagsAvailable) result.Notes.Add("この記録にはIoFlagsがありません（旧形式）。ページングI/Oとアプリ要求を区別できないため、すべてのRead/Writeを再生します。");
        else result.Notes.Add(options.Paging switch
        {
            PagingReplay.None=>"ページングI/Oは再生しません。",
            PagingReplay.All=>"ページングI/Oもすべて再生します（キャッシュ補充・Lazy Writerの二重再生を含み得ます）。",
            _=>"ページングReadは、同じファイルの同じ1MiB領域に前後2秒以内のアプリReadがない場合だけmmap由来として再生します。ページングWriteは再生しません。"
        });
        using var executor=new ReplayExecutor(dataset,m,accessPlan,unsafePaths);
        using var workers=new SemaphoreSlim(options.Workers);
        // Started at the first replayable event: EventOrder runs its external sort before yielding, which must not eat into the timeline.
        var clock=new Stopwatch(); long baseUs=0;
        var runId=DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6];
        string outcomePath=Path.Combine(session,"replay-"+runId+".jsonl"); result.OutcomeFile=Path.GetFileName(outcomePath);
        using var output=new StreamWriter(outcomePath,false,new UTF8Encoding(false)) { AutoFlush=false };
        // pacing: how much later than planned each request was issued (Timeline: the recorded time; ThinkTime: the recorded gap
        // after the same thread's previous request, from the moment that request really finished).
        var latency=new LatencyHistogram(); var pacing=new LatencyHistogram();
        using var timer=new PreciseTimer(clock,token);
        var threadLast=new Dictionary<(int,int),(Task<double> Finished,long RecordedEnd)>();
        var handleLast=new Dictionary<string,Task>();
        // Order between threads that the recording shows through names: an operation on a path waits for the last change of
        // that name (a create, a rename to or from it, a delete), and a change waits for the last operation on the name. Without
        // this, a thread could open a file another thread had not created yet whenever that thread lagged.
        var pathLast=new Dictionary<string,Task>(StringComparer.OrdinalIgnoreCase);
        var pathChange=new Dictionary<string,Task>(StringComparer.OrdinalIgnoreCase);
        // Requests prepared ahead of their time: enough that a slow request (a flush of tens of ms) holds up only its own thread
        // and file, not every other thread behind it.
        int lookAhead=Math.Max(LookAhead,options.Workers*4);
        using var slots=new SemaphoreSlim(lookAhead);
        var active=new HashSet<Task>(); var gate=new object();
        // Recorded before the first request, so a reset also knows about runs that were cancelled or crashed.
        m.State="Replaying"; m.Runs.Add(outcomePath); JsonStore.Write(marker,m);
        void Record(IoEvent captured,ReplayOutcome outcome)
        {
            lock(gate)
            {
                bool sent=outcome.Status is "Executed" or "ReproducedFailure" or "FailureNotReproduced";
                if(outcome.Status=="Executed")
                {
                    if(captured.Operation==IoOperation.Read) result.ReadBytes+=outcome.Transferred;
                    if(captured.Operation==IoOperation.Write) result.WriteBytes+=outcome.Transferred;
                    if(captured.Operation is IoOperation.Read or IoOperation.Write) latency.Add(outcome.DurationMs);
                }
                if(sent) result.Executed++;
                if(outcome.Status=="ReproducedFailure") result.ReproducedFailures++;
                else if(outcome.Status=="FailureNotReproduced") result.FailuresNotReproduced++;
                else if(outcome.Status=="Failed") result.Failed++;
                else if(!sent) result.Skipped++;
                if(sent || outcome.Status=="Failed")
                {
                    double late=Math.Max(0,(outcome.ActualUs-outcome.PlannedUs)/1000.0);
                    result.MaxSchedulingLagMs=Math.Max(result.MaxSchedulingLagMs,late); pacing.Add(late);
                }
                output.WriteLine(JsonSerializer.Serialize(outcome,JsonStore.Options));
                // Handed to the OS regularly, so that a reset after a crash still finds what was sent.
                if((result.Executed+result.Failed+result.Skipped)%512==0)
                { output.Flush(); progress?.Report($"再生中 · 実行 {result.Executed:N0} / 失敗 {result.Failed:N0} / 対象外 {result.Skipped:N0}"); }
            }
        }
        using var reporterStop=new CancellationTokenSource();
        var reporterTask=Task.CompletedTask;
        long lastBytes=0; double lastSeconds=0;
        WorkProgress Snapshot(string stage)
        {
            lock(gate)
            {
                double seconds=clock.Elapsed.TotalSeconds,interval=Math.Max(.001,seconds-lastSeconds);
                long bytes=result.ReadBytes+result.WriteBytes; double rate=(bytes-lastBytes)/1048576.0/interval; lastBytes=bytes; lastSeconds=seconds;
                return new(stage,result.Executed+result.Failed+result.Skipped,total,
                    $"実行 {result.Executed:N0} / 失敗 {result.Failed:N0} / 対象外 {result.Skipped:N0} · Read {result.ReadBytes/1048576.0:N0} MiB · Write {result.WriteBytes/1048576.0:N0} MiB · 直近 {rate:N0} MiB/s · 経過 {seconds:N0} 秒");
            }
        }
        Task StartReporter()=>work is null?Task.CompletedTask:Task.Run(async()=>
        {
            try { while(true) { await Task.Delay(500,reporterStop.Token); work.Report(Snapshot("実行中")); } }
            catch(OperationCanceledException) { }
        });
        work?.Report(new("イベントを並べ替え中",0,0,$"{total:N0} 操作を投入順に並べています"));
        try
        {
            foreach(var e in EventOrder.Read(session,token))
            {
                token.ThrowIfCancellationRequested();
                if(!e.IsFileIo || options.ProcessOnly&&!e.MatchesProcess || options.RootOnly&&!e.MatchesRoot || !locations.Includes(e.Path)) continue;
                if(!clock.IsRunning) { if(options.TrimLeadingIdle) baseUs=e.StartUs; clock.Start(); reporterTask=StartReporter(); }
                long plannedUs=(long)((e.StartUs-baseUs)/options.Speed);
                // Another process's I/O on the target's files is the recorded machine's own (its cache, antivirus): kept for
                // analysis, not replayed; the test target's OS does its own.
                if((e.ByOtherProcess?"SkippedOtherProcess":paging.FlagsAvailable?paging.SkipReason(e,options.Paging):null) is { } skip)
                { Record(e,new ReplayOutcome { Sequence=e.Sequence,Operation=e.Operation,Path=e.Path,PlannedUs=plannedUs,ActualUs=(long)(clock.Elapsed.TotalMilliseconds*1000),Status=skip }); continue; }
                await slots.WaitAsync(token);
                // A kernel file object is system-wide: another process using the app's file object (an antivirus or the
                // search indexer querying it) must follow the app's earlier operations on it, even when the app's thread lags.
                var threadKey=(e.ProcessId,e.ThreadId); var handleKey=e.FileObject;
                var priorThread=threadLast.GetValueOrDefault(threadKey,(NotStarted,baseUs));
                var priorHandle=string.IsNullOrEmpty(e.FileObject)?Task.CompletedTask:handleLast.GetValueOrDefault(handleKey,Task.CompletedTask);
                var names=Names(e); bool change=ChangesName(e);
                var priorNames=names.Select(n=>change?pathLast.GetValueOrDefault(n):pathChange.GetValueOrDefault(n)).OfType<Task>().ToArray();
                var priorName=priorNames.Length switch { 0=>Task.CompletedTask,1=>priorNames[0],_=>Task.WhenAll(priorNames) };
                long think=Math.Max(0,e.StartUs-priorThread.Item2);
                var captured=e;
                // Each task returns when its request finished on the replay clock (µs); ThinkTime measures the next gap from there.
                var task=Task.Run(async()=>
                {
                    var outcome=new ReplayOutcome { Sequence=captured.Sequence,Operation=captured.Operation,Path=captured.Path,PlannedUs=plannedUs };
                    bool acquired=false; double finished=0;
                    try
                    {
                        double previous=await priorThread.Item1; await priorHandle; await priorName;
                        // ThinkTime: the recorded gap after this thread's previous request, counted from the moment that request
                        // really returned, so the replay's own bookkeeping falls inside the gap instead of adding to it.
                        if(options.Mode==ReplayMode.ThinkTime) outcome.PlannedUs=(long)(previous+think/options.Speed);
                        await timer.WaitUntil(outcome.PlannedUs);
                        await workers.WaitAsync(token); acquired=true;
                        outcome.ActualUs=(long)clock.Elapsed.TotalMicroseconds;
                        var operationClock=Stopwatch.StartNew();
                        try { (outcome.Status,outcome.Transferred)=executor.Execute(captured); }
                        finally { outcome.DurationMs=operationClock.Elapsed.TotalMilliseconds; finished=clock.Elapsed.TotalMicroseconds; }
                    }
                    catch(OperationCanceledException) { outcome.Status="Cancelled"; }
                    catch(Exception ex) { outcome.Status="Failed"; outcome.Error=ex.Message; }
                    finally { if(acquired) workers.Release(); }
                    try { Record(captured,outcome); } finally { slots.Release(); }
                    return finished>0?finished:clock.Elapsed.TotalMicroseconds;
                });
                threadLast[threadKey]=(task,captured.EndUs??captured.StartUs);
                if(!string.IsNullOrEmpty(e.FileObject)) handleLast[handleKey]=task;
                foreach(var name in names) { pathLast[name]=task; if(change) pathChange[name]=task; }
                active.Add(task);
                if(active.Count>=lookAhead*2) active.RemoveWhere(t=>t.IsCompleted);
            }
        }
        catch(OperationCanceledException) { result.Cancelled=true; }
        finally
        {
            await Task.WhenAll(active);
            reporterStop.Cancel(); await reporterTask;
            work?.Report(Snapshot(token.IsCancellationRequested||result.Cancelled?"中止しました":"完了"));
            await output.FlushAsync();
            result.ElapsedSeconds=clock.Elapsed.TotalSeconds; result.P95Ms=latency.Percentile(.95); result.P99Ms=latency.Percentile(.99);
            result.TemporaryOpens=executor.TemporaryOpens;
            if(result.ReproducedFailures+result.FailuresNotReproduced>0)
                result.Notes.Add($"記録で失敗した要求のうち、ダミーでも同じ形で届くもの（無い名前の確認、フォルダをファイルとして開く、末尾より先の読み取り、一覧の終わり、属性の問い合わせ）を再生しました: 同じく失敗 {result.ReproducedFailures:N0} / 失敗しなかった {result.FailuresNotReproduced:N0}。");
            if(result.TemporaryOpens>0)
                result.Notes.Add($"開いた記録がないファイルへの要求 {result.TemporaryOpens:N0} 件は、その要求のためだけに開いて閉じました（元の記録より Open/Close が多くなります）。");
            result.PacingErrorP50Ms=pacing.Percentile(.5); result.PacingErrorP95Ms=pacing.Percentile(.95);
            result.Cancelled|=token.IsCancellationRequested;
            m.State="Consumed"; JsonStore.Write(marker,m);
            JsonStore.Write(Path.Combine(session,"replay-"+runId+".json"),result);
            // The results are read as replay-*.jsonl or, once compressed, replay-*.jsonl.gz.
            output.Dispose(); LogCompressor.Enqueue(outcomePath);
            progress?.Report($"再生完了 · {result.ElapsedSeconds:N1} 秒 / 実行 {result.Executed:N0} / 失敗 {result.Failed:N0} / P95 {result.P95Ms:N2} ms");
        }
        return result;
    }
}

// A paging read that touches the same 1 MiB block as an application read within ±2 s is a cache fill or read-ahead of that read;
// replaying both would double the load. Paging reads with no such neighbour are treated as memory-mapped access.
internal sealed class PagingClassifier
{
    private const int BlockShift=20, MaxBlocks=64;
    private const long WindowUs=2_000_000;
    private readonly Dictionary<string,Dictionary<long,List<long>>> _reads=new(StringComparer.OrdinalIgnoreCase);
    public bool FlagsAvailable { get; private set; }
    public void Observe(IoEvent e)
    {
        if(e.IoFlags.HasValue) FlagsAvailable=true;
        if(e.Operation!=IoOperation.Read || e.Failed || e.IsPagingIo || e.Offset<0 || string.IsNullOrEmpty(e.Path)) return;
        if(!_reads.TryGetValue(e.Path,out var blocks)) _reads[e.Path]=blocks=[];
        foreach(var block in Blocks(e)) { if(!blocks.TryGetValue(block,out var times)) blocks[block]=times=[]; times.Add(e.StartUs); }
    }
    public void Seal() { foreach(var blocks in _reads.Values) foreach(var times in blocks.Values) times.Sort(); }
    public string? SkipReason(IoEvent e,PagingReplay mode)
    {
        if(mode==PagingReplay.All || !e.IsPagingIo || e.Operation is not (IoOperation.Read or IoOperation.Write)) return null;
        if(mode==PagingReplay.None) return "SkippedPagingIo";
        if(e.Operation==IoOperation.Write) return "SkippedPagingWrite";
        if(_reads.TryGetValue(e.Path,out var blocks))
            foreach(var block in Blocks(e))
                if(blocks.TryGetValue(block,out var times) && Near(times,e.StartUs)) return "SkippedPagingCacheFill";
        return null;
    }
    private static IEnumerable<long> Blocks(IoEvent e)
    {
        long first=e.Offset>>BlockShift,last=(e.Offset+Math.Max(1,e.Size)-1)>>BlockShift;
        for(long b=first;b<=last && b-first<MaxBlocks;b++) yield return b;
    }
    private static bool Near(List<long> sorted,long time)
    {
        int lo=0,hi=sorted.Count;
        while(lo<hi) { int mid=(lo+hi)>>1; if(sorted[mid]<time-WindowUs) lo=mid+1; else hi=mid; }
        return lo<sorted.Count && sorted[lo]<=time+WindowUs;
    }
}

internal sealed class ReplayExecutor : IDisposable
{
    private readonly string _root;
    private readonly DatasetManifest _manifest;
    private readonly Dictionary<string,FileAccess> _accessPlan;
    private readonly ConcurrentDictionary<string,SafeFileHandle> _handles=[];
    // Paths are validated against the file system once before the run (RunAsync); per request only memory is consulted,
    // so the replay issues exactly the recorded requests and no extra opens or attribute queries on the measured storage.
    private readonly Dictionary<string,string> _resolved=new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string,byte> _directories=new(StringComparer.OrdinalIgnoreCase);
    // Recorded paths whose dataset path is unsafe (outside the dummy or through a link): their operations are skipped.
    private readonly HashSet<string> _unsafe=new(StringComparer.OrdinalIgnoreCase);
    public ReplayExecutor(string root,DatasetManifest manifest,Dictionary<string,FileAccess> accessPlan,IReadOnlySet<string>? unsafeRelatives=null)
    {
        _root=Path.GetFullPath(root); _manifest=manifest; _accessPlan=accessPlan;
        foreach(var (original,relative) in manifest.Paths)
        {
            if(unsafeRelatives?.Contains(relative)==true || !PathPolicy.TryCombineInside(_root,relative,out var full)) _unsafe.Add(original);
            else _resolved[original]=full;
        }
        _directories[Path.TrimEndingDirectorySeparator(_root)]=0;
        foreach(var relative in manifest.Directories)
            if(unsafeRelatives?.Contains(relative)!=true && PathPolicy.TryCombineInside(_root,relative,out var full)) AddDirectory(full);
        foreach(var relative in manifest.InitialSizes.Keys)
            if(unsafeRelatives?.Contains(relative)!=true && PathPolicy.TryCombineInside(_root,relative,out var full)) AddDirectory(Path.GetDirectoryName(full)!);
    }
    internal static string AccessKey(IoEvent e)=>e.ProcessId+":"+e.FileObject+":"+e.Path.ToUpperInvariant();
    private string Resolve(string path)=>_resolved.TryGetValue(path,out var full)?full:throw new NotSupportedException("未解決のパスです。");
    private void AddDirectory(string directory)
    {
        for(var d=directory;d is not null && PathPolicy.IsUnder(d,_root) && _directories.TryAdd(d,0);d=Path.GetDirectoryName(d)) { }
    }
    // Creates the parent folder only when this run does not already know it exists (one call, the first time).
    private void EnsureParent(string path)
    {
        var parent=Path.GetDirectoryName(path)!;
        if(_directories.ContainsKey(parent)) return;
        Directory.CreateDirectory(parent); AddDirectory(parent);
    }
    // Recorded failures that reach the storage again the same way on the dummy: a lookup of a name that does not exist (yet),
    // a folder opened as a file, a read at or past the end of a file, the end of a folder listing, an attribute query. Other
    // failures depend on state the dummy does not have (locks, permissions, a full disk) or would change it, and are skipped.
    internal static bool ReplaysFailure(IoEvent e)=>e.Operation switch
    {
        IoOperation.Open=>e.CreateDisposition==1 && e.NtStatus is NameNotFound or PathNotFound or FileIsADirectory,
        IoOperation.Read=>e.NtStatus==EndOfFile && !e.IsPagingIo,
        IoOperation.DirectoryEnumerate=>e.NtStatus is NoMoreFiles or NoSuchFile,
        IoOperation.QueryInfo=>true,
        _=>false
    };
    private const uint NameNotFound=0xC0000034,PathNotFound=0xC000003A,FileIsADirectory=0xC00000BA,EndOfFile=0xC0000011,NoMoreFiles=0x80000006,NoSuchFile=0xC000000F;
    private const int BufferOverflow=unchecked((int)0x80000005);
    private long _temporaryOpens;
    public long TemporaryOpens=>Interlocked.Read(ref _temporaryOpens);
    public (string Status,long Bytes) Execute(IoEvent e)
    {
        if(e.IsFastIoRefusal) return ("SkippedFastIoRetry",0);
        if(e.Failed && !ReplaysFailure(e)) return ("SkippedRecordedFailure",0);
        if(string.IsNullOrEmpty(e.Path) || !_manifest.Paths.ContainsKey(e.Path)) return ("SkippedUnresolvedPath",0);
        if(_unsafe.Contains(e.Path) || e.Operation==IoOperation.Rename && e.DestinationPath is { } moved && _unsafe.Contains(moved)) return ("SkippedUnsafePath",0);
        if(e.Operation is IoOperation.SetInfo or IoOperation.FsControl) return ("SkippedUnsupportedInfoClass",0);
        // A change-notification request stays pending on the folder; it is recorded to show which folders are watched.
        if(e.Operation==IoOperation.DirectoryNotify) return ("SkippedUnsupportedOperation",0);
        if(e.Operation==IoOperation.Rename && (string.IsNullOrEmpty(e.DestinationPath) || !_resolved.ContainsKey(e.DestinationPath))) return ("SkippedUnresolvedRename",0);
        string path=Resolve(e.Path),key=e.ProcessId+":"+e.FileObject;
        if(e.Failed) return ReplayFailure(e,path,key);
        if(e.Operation is IoOperation.Close or IoOperation.Cleanup)
        { if(_handles.TryRemove(key,out var h)) h.Dispose(); return ("Executed",0); }
        if(e.Operation==IoOperation.Open)
        {
            if(_handles.TryRemove(key,out var old)) old.Dispose();
            // Directories are also opened without FILE_DIRECTORY_FILE (FILE_FLAG_BACKUP_SEMANTICS); the dataset tells which they are.
            // An existing folder is opened like the original did, and the handle serves its later queries and listings.
            if(e.IsDirectory || _directories.ContainsKey(path))
            {
                if(_directories.ContainsKey(path))
                {
                    var folder=OpenMetadata(path,ListDirectory);
                    if(!folder.IsInvalid) { _handles[key]=folder; return ("Executed",0); }
                    int error=Marshal.GetLastWin32Error(); folder.Dispose();
                    if(e.CreateDisposition is not (2 or 3 or 5)) throw new System.ComponentModel.Win32Exception(error);
                }
                else if(e.CreateDisposition is not (2 or 3 or 5)) throw new DirectoryNotFoundException(path);
                CreateFolder(path); return ("Executed",0);
            }
            EnsureParent(path);
            var mode=e.CreateDisposition switch { 0=>FileMode.Create,1=>FileMode.Open,2=>FileMode.CreateNew,3=>FileMode.OpenOrCreate,4=>FileMode.Truncate,5=>FileMode.Create,_=>FileMode.Open };
            var flags=FileOptions.RandomAccess;
            if((e.CreateOptions.GetValueOrDefault()&0x2)!=0) flags|=FileOptions.WriteThrough;
            if((e.CreateOptions.GetValueOrDefault()&0x4)!=0) flags= (flags&~FileOptions.RandomAccess)|FileOptions.SequentialScan;
            if((e.CreateOptions.GetValueOrDefault()&0x1000)!=0) flags|=FileOptions.DeleteOnClose;
            var share=(FileShare)(e.ShareAccess??7);
            // DesiredAccess is not in classic FileIO_Create. Infer it from the operations observed on the handle: writes need
            // write access, reads read access; a handle with neither (attributes and sizes only) is opened for attributes,
            // which antivirus does not scan on open as it does a data open.
            if(mode==FileMode.Open && !_accessPlan.ContainsKey(AccessKey(e)) && (flags&FileOptions.DeleteOnClose)==0)
            {
                var attributes=CreateFile(Long(path),ReadAttributes|ReadEa,share,0,3,0,0);
                if(attributes.IsInvalid) { int error=Marshal.GetLastWin32Error(); attributes.Dispose(); throw new System.ComponentModel.Win32Exception(error); }
                _handles[key]=attributes; return ("Executed",0);
            }
            var access=mode==FileMode.Open?_accessPlan.GetValueOrDefault(AccessKey(e),FileAccess.Read):FileAccess.ReadWrite;
            _handles[key]=File.OpenHandle(path,mode,access,share,flags);
            return ("Executed",0);
        }
        if(e.Operation==IoOperation.Rename)
        {
            string target=Resolve(e.DestinationPath!); EnsureParent(target);
            if(_directories.ContainsKey(path)) { Directory.Move(path,target); _directories.TryRemove(path,out _); AddDirectory(target); }
            // Only successful renames are replayed: one onto an existing name succeeded because it replaced it
            // (write to a temporary file, then swap it in), so the replay replaces it as well.
            else File.Move(path,target,true);
            return ("Executed",0);
        }
        if(e.Operation==IoOperation.Delete)
        {
            if(e.InfoClass is not (13 or 64)) return ("SkippedUnknownDeleteSemantics",0);
            // Record delete-on-close intent on a handle instead of deleting the name prematurely.
            if(_handles.TryRemove(key,out var previous)) previous.Dispose();
            var handle=CreateFile(Long(path),0x00010000,FileShare.ReadWrite|FileShare.Delete,0,3,0,0);
            if(handle.IsInvalid) { handle.Dispose(); throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
            try
            {
                var info=new FileDisposition { DeleteFile=e.ExtraInfo.GetValueOrDefault()!=0 };
                if(!SetDisposition(handle,4,ref info,(uint)Marshal.SizeOf<FileDisposition>())) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                _handles[key]=handle;
            }
            catch { handle.Dispose(); throw; }
            return ("Executed",0);
        }
        if(e.Operation==IoOperation.QueryInfo)
        {
            int status=Query(e,path,key);
            if(Failed(status)) throw new IOException($"属性の問い合わせが失敗しました（NTSTATUS 0x{status:X8}、情報クラス {e.InfoClass}）。");
            return ("Executed",0);
        }
        // The listing may end earlier than recorded: the dummy folder holds only the files the record touched.
        if(e.Operation==IoOperation.DirectoryEnumerate) { List(e,path,key); return ("Executed",0); }
        bool temporary=!_handles.TryGetValue(key,out var file);
        if(temporary) Interlocked.Increment(ref _temporaryOpens);
        file ??=OpenExisting(path,e.Operation==IoOperation.Read?FileAccess.Read:FileAccess.ReadWrite);
        try
        {
            if(e.Operation==IoOperation.SetEndOfFile)
            { if(e.Size<0) throw new InvalidDataException("EOFが負の値です。"); RandomAccess.SetLength(file,e.Size); return ("Executed",0); }
            if(e.Operation==IoOperation.SetAllocation)
            { var info=new FileAllocation { AllocationSize=e.Size }; if(e.Size<0 || !SetAllocation(file,5,ref info,8)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); return ("Executed",0); }
            if(e.Operation==IoOperation.Flush) { RandomAccess.FlushToDisk(file); return ("Executed",0); }
            if(e.Operation is not (IoOperation.Read or IoOperation.Write)) return ("SkippedUnsupportedOperation",0);
            if(e.Offset<0 || e.Size<0 || e.Size>uint.MaxValue || e.Offset>long.MaxValue-e.Size) throw new InvalidDataException("I/Oの範囲が不正です。");
            // One recorded request becomes one RandomAccess call; never silently split its size distribution.
            if(e.Size>64*1024*1024) return ("SkippedRequestAbove64MiB",0);
            byte[] buffer=ArrayPool<byte>.Shared.Rent((int)Math.Max(1,e.Size));
            long transferred=0; var random=new DeterministicData(_manifest.Seed,e.Path+":"+e.Sequence);
            try
            {
                int n=(int)e.Size;
                if(e.Operation==IoOperation.Read) transferred=RandomAccess.Read(file,buffer.AsSpan(0,n),e.Offset);
                else { random.Fill(buffer.AsSpan(0,n)); RandomAccess.Write(file,buffer.AsSpan(0,n),e.Offset); transferred=n; }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            if(e.Transferred is { } expected && e.Operation==IoOperation.Read && transferred!=expected)
                throw new IOException($"Read転送量が記録と異なります: 期待 {expected} / 実際 {transferred} bytes");
            return ("Executed",transferred);
        }
        finally { if(temporary) file.Dispose(); }
    }
    // A recorded failure is sent again as the same kind of request; whether it failed again is reported, never forced.
    private (string Status,long Bytes) ReplayFailure(IoEvent e,string path,string key)
    {
        bool failed;
        switch(e.Operation)
        {
            case IoOperation.Open:
                // As a file (FILE_NON_DIRECTORY_FILE) unless the original asked for a folder, like CreateFile does.
                using(var handle=CreateFile(Long(path),ReadAttributes,FileShare.ReadWrite|FileShare.Delete,0,3,e.IsDirectory?BackupSemantics:0,0)) failed=handle.IsInvalid;
                break;
            case IoOperation.QueryInfo: failed=Failed(Query(e,path,key)); break;
            case IoOperation.DirectoryEnumerate: failed=Failed(List(e,path,key)); break;
            default:
                if(e.Size is < 0 or > 64*1024*1024 || e.Offset<0) return ("SkippedRecordedFailure",0);
                failed=WithHandle(path,key,FileAccess.Read,file=>
                {
                    byte[] buffer=ArrayPool<byte>.Shared.Rent((int)Math.Max(1,e.Size));
                    try { return RandomAccess.Read(file,buffer.AsSpan(0,(int)e.Size),e.Offset); }
                    finally { ArrayPool<byte>.Shared.Return(buffer); }
                })==0;
                break;
        }
        return (failed?"ReproducedFailure":"FailureNotReproduced",0);
    }
    private static bool Failed(int status)=>status<0 && status!=BufferOverflow;
    // The recorded information class, as one request on the handle the original used. Without a recorded open for that file
    // object (opened before the recording, or by the system), a handle is opened for this request only and counted.
    private int Query(IoEvent e,string path,string key)
    {
        byte[] buffer=ArrayPool<byte>.Shared.Rent(QueryBuffer);
        try { return WithMetadataHandle(path,key,0,handle=>NtQueryInformationFile(handle,out _,buffer,QueryBuffer,e.InfoClass??FileBasicInformation)); }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    // One NtQueryDirectoryFile call with the recorded buffer size, class and pattern, continuing the listing on that handle.
    private int List(IoEvent e,string path,string key)
    {
        int length=e.Size>0?(int)Math.Clamp(e.Size,1024,1<<20):65536;
        byte[] buffer=ArrayPool<byte>.Shared.Rent(length);
        nint text=string.IsNullOrEmpty(e.SearchPattern)?0:Marshal.StringToHGlobalUni(e.SearchPattern);
        try
        {
            int infoClass=e.InfoClass is > 0 and < 100 ? e.InfoClass.Value : FileBothDirectoryInformation;
            return WithMetadataHandle(path,key,ListDirectory,handle=>
            {
                if(text==0) return NtQueryDirectoryFile(handle,0,0,0,out _,buffer,(uint)length,infoClass,false,0,false);
                var name=new UnicodeString { Length=(ushort)(e.SearchPattern!.Length*2),MaximumLength=(ushort)(e.SearchPattern.Length*2+2),Buffer=text };
                return NtQueryDirectoryFile(handle,0,0,0,out _,buffer,(uint)length,infoClass,false,ref name,false);
            });
        }
        finally { if(text!=0) Marshal.FreeHGlobal(text); ArrayPool<byte>.Shared.Return(buffer); }
    }
    private T WithHandle<T>(string path,string key,FileAccess access,Func<SafeFileHandle,T> request)
    {
        if(_handles.TryGetValue(key,out var handle)) return request(handle);
        Interlocked.Increment(ref _temporaryOpens);
        using var temporary=OpenExisting(path,access);
        return request(temporary);
    }
    private T WithMetadataHandle<T>(string path,string key,uint access,Func<SafeFileHandle,T> request)
    {
        if(_handles.TryGetValue(key,out var handle)) return request(handle);
        Interlocked.Increment(ref _temporaryOpens);
        using var temporary=OpenMetadata(path,access);
        if(temporary.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return request(temporary);
    }
    // Attributes (and, for a folder, listing) only: such a handle does not take part in sharing checks for file data.
    private static SafeFileHandle OpenMetadata(string path,uint access)=>CreateFile(Long(path),access|ReadAttributes|ReadEa,FileShare.ReadWrite|FileShare.Delete,0,3,BackupSemantics,0);
    // One request like the original's create; Directory.CreateDirectory would first look up the folder and each missing parent.
    private void CreateFolder(string path)
    {
        if(!CreateDirectoryW(Long(path),0))
        {
            int error=Marshal.GetLastWin32Error();
            if(error==3) { EnsureParent(path); error=CreateDirectoryW(Long(path),0)?0:Marshal.GetLastWin32Error(); }
            if(error is not (0 or 183)) throw new System.ComponentModel.Win32Exception(error);
        }
        AddDirectory(path);
    }
    // Win32 calls made here directly need the extended prefix for long paths (File.* adds it by itself).
    private static string Long(string path)=>path.StartsWith(@"\\?\",StringComparison.Ordinal)?path:path.StartsWith(@"\\",StringComparison.Ordinal)?@"\\?\UNC\"+path[2..]:@"\\?\"+path;
    private const uint ReadAttributes=0x80,ReadEa=0x8,ListDirectory=0x1,BackupSemantics=0x02000000;
    private const int QueryBuffer=64*1024,FileBasicInformation=4,FileBothDirectoryInformation=3;
    private static SafeFileHandle OpenExisting(string path,FileAccess access) => File.OpenHandle(path,FileMode.Open,access,FileShare.ReadWrite|FileShare.Delete,FileOptions.RandomAccess);
    public void Dispose() { foreach(var h in _handles.Values) h.Dispose(); _handles.Clear(); }
    [StructLayout(LayoutKind.Sequential)] private struct FileAllocation { public long AllocationSize; }
    [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { [MarshalAs(UnmanagedType.U1)] public bool DeleteFile; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatusBlock { public nint Status; public nint Information; }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length; public ushort MaximumLength; public nint Buffer; }
    [DllImport("kernel32.dll",EntryPoint="SetFileInformationByHandle",SetLastError=true)] private static extern bool SetAllocation(SafeFileHandle handle,int infoClass,ref FileAllocation info,uint size);
    [DllImport("kernel32.dll",EntryPoint="SetFileInformationByHandle",SetLastError=true)] private static extern bool SetDisposition(SafeFileHandle handle,int infoClass,ref FileDisposition info,uint size);
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateFile(string path,uint access,FileShare share,nint security,uint mode,uint flags,nint template);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CreateDirectoryW(string path,nint security);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationFile(SafeFileHandle handle,out IoStatusBlock status,byte[] buffer,uint length,int infoClass);
    [DllImport("ntdll.dll")] private static extern int NtQueryDirectoryFile(SafeFileHandle handle,nint evt,nint apc,nint apcContext,out IoStatusBlock status,byte[] buffer,uint length,int infoClass,[MarshalAs(UnmanagedType.U1)] bool single,ref UnicodeString name,[MarshalAs(UnmanagedType.U1)] bool restart);
    [DllImport("ntdll.dll")] private static extern int NtQueryDirectoryFile(SafeFileHandle handle,nint evt,nint apc,nint apcContext,out IoStatusBlock status,byte[] buffer,uint length,int infoClass,[MarshalAs(UnmanagedType.U1)] bool single,nint name,[MarshalAs(UnmanagedType.U1)] bool restart);
}
