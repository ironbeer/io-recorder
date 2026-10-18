using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace IoRecorder.Core;

public sealed class EtwRecorder
{
    public LiveMetrics Metrics { get; }=new();
    public SessionManifest Manifest { get; private set; }=new();
    public MemorySample? LatestMemory { get; private set; }
    public long EventsWritten => Interlocked.Read(ref _written);
    public long QueueEventsDropped => Interlocked.Read(ref _dropped);
    public long RootMatchedEvents => Interlocked.Read(ref _rootMatched);
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    // Free space left on the drive of the record: a warning below the first, the recording is stopped and kept below the second.
    public const long WarnFreeBytes=5L<<30, StopFreeBytes=1L<<30;
    private const string SessionPrefix="IORecorder-";
    private const int KeyPathLimit=2_000_000, UnresolvedSampleSize=2000;
    private static readonly Guid FileIoTaskGuid=new("90cbdc39-4a3e-11d1-84f4-0000f80464e3");
    private long _sequence,_dropped,_unresolved,_written,_rootMatched;
    private volatile bool _stoppedForSpace;
    private readonly Stopwatch _clock=new();
    private long NowUs => (long)(_clock.Elapsed.TotalMilliseconds*1000);

    // A kernel session lives on after its process ends (a crash, a forced close) and keeps its buffers and CPU until stopped.
    // Sessions of this tool whose process is gone are stopped; returns how many.
    public static int StopOrphanSessions()
    {
        if(!IsAdministrator) return 0;
        List<string> names;
        try { names=TraceEventSession.GetActiveSessionNames(); }
        catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { return 0; }
        int stopped=0;
        foreach(var name in names)
        {
            var parts=name.Split('-');
            if(!name.StartsWith(SessionPrefix,StringComparison.Ordinal) || parts.Length<3 || !int.TryParse(parts[1],out int pid) || pid==Environment.ProcessId) continue;
            bool alive;
            try { using var p=Process.GetProcessById(pid); alive=!p.HasExited && p.ProcessName.StartsWith("IORecorder",StringComparison.OrdinalIgnoreCase); }
            catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { alive=false; }
            if(alive) continue;
            try { using var orphan=new TraceEventSession(name,TraceEventSessionOptions.Attach); orphan.Stop(true); stopped++; }
            catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or FileNotFoundException) { }
        }
        return stopped;
    }

    public async Task<AnalysisResult> RecordAsync(string output,RecordingOptions options,string label,CancellationToken token,IProgress<string>? progress=null,Action? ready=null)
    {
        if(!IsAdministrator) throw new UnauthorizedAccessException("ETW計測には管理者権限が必要です。管理者として起動してください。");
        output=Path.GetFullPath(output); options.Validate(output);
        if(Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("記録先は新しいフォルダまたは空のフォルダを指定してください。");
        Directory.CreateDirectory(output);
        // Debug: other processes' handles and the addresses of their files (OpenFiles); profile: the memory lists (SystemSampler).
        Privileges.Enable("SeDebugPrivilege","SeProfileSingleProcessPrivilege");
        if(StopOrphanSessions() is > 0 and var orphans) progress?.Report($"前回途中で止まった計測セッションを {orphans} 件止めました。");
        var tracker=new ProcessTracker(options); var mapper=new DevicePathMapper(); var roots=new RootMatcher(options.ResolvedRoots);
        using(var self=Process.GetCurrentProcess())
            Manifest=new() { Options=options,Label=label,TotalPhysicalBytes=MemorySampler.Sample(0).TotalBytes,RecorderProcessId=Environment.ProcessId,RecorderStartedUtc=self.StartTime.ToUniversalTime() };
        Manifest.Warnings.Add("ファイルサイズはETW受信後の非同期照会値です。初期サイズの保証はありません。");
        Manifest.Warnings.Add("mmapの全参照、LockFile APIの引数、クラウド通信はETWファイルI/Oから完全には復元できません。");
        foreach(var r in options.ResolvedRoots.Where(r=>r.DevicePath is null && r.UncPath is null && r.UserPath.Length>1 && r.UserPath[1]==':' && !Directory.Exists(r.UserPath)))
            Manifest.Warnings.Add($"このプロセスから {r.UserPath} が見えません。昇格前に解決したデバイスパスがないため、ETWのパスと一致しない可能性があります。");
        JsonStore.Write(Path.Combine(output,"session.json"),Manifest);
        var environment=Task.Run(()=>
        {
            try { JsonStore.Write(Path.Combine(output,"environment.json"),EnvironmentInfo.Collect(options,output)); }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
        });
        var devicePrefixes=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
        string MapPath(string raw,out bool rootMatch)
        {
            var mapped=mapper.Map(raw);
            rootMatch=mapped.Length>0 && roots.Match(raw,mapped,out mapped);
            return mapped;
        }
        string Display(string raw,out bool rootMatch)
        {
            var mapped=MapPath(raw,out rootMatch);
            if(!rootMatch && mapped.StartsWith(@"\Device\",StringComparison.OrdinalIgnoreCase) && (devicePrefixes.Count<1024 || devicePrefixes.ContainsKey(DevicePrefix(mapped))))
            { var prefix=DevicePrefix(mapped); devicePrefixes[prefix]=devicePrefixes.GetValueOrDefault(prefix)+1; }
            return mapped;
        }
        // Files the target process uses, by file (FileKey) and by handle (file object): other processes' I/O on them is kept.
        var targetKeys=new HashSet<ulong>(); var targetObjects=new HashSet<ulong>();
        var openPaths=new Dictionary<ulong,string>();
        var running=tracker.TargetPids();
        if(options.Mode!=CaptureMode.Folder && running.Length>0)
        {
            var names=string.Join("、",running.Select(tracker.Name).Distinct(StringComparer.OrdinalIgnoreCase));
            Manifest.Warnings.Add($"対象のアプリ（{names}）は記録の開始前から起動していました。開始時に開いていたファイルは確認してパスを補いますが、補えなかったI/Oは「パス不明」として数えます。確実に取るには、アプリより先に記録を開始してください。");
            progress?.Report($"対象のアプリ（{names}）は記録の開始前から起動しています。アプリより先に記録を開始すると取りこぼしがありません。");
        }
        if(options.OpenFilesAtStart)
        {
            progress?.Report("記録の開始時に開いているファイルを確認しています…");
            var (files,problem)=await Task.Run(()=>OpenFiles.Take(pid=>options.Mode!=CaptureMode.Process || tracker.Matches(pid),TimeSpan.FromSeconds(15)));
            if(problem is not null) Manifest.Warnings.Add("開始時に開いていたファイルの確認: "+problem);
            using var list=new StreamWriter(Path.Combine(output,"open-files.jsonl"),false,new UTF8Encoding(false));
            foreach(var f in files)
            {
                string path=MapPath(f.Path,out bool rm); bool pm=tracker.Matches(f.Pid);
                if(!(options.Mode switch { CaptureMode.Process=>pm,CaptureMode.Folder=>rm,_=>pm||rm }) || PathPolicy.IsUnder(path,output)) continue;
                openPaths[f.FileObject]=path; if(pm) targetObjects.Add(f.FileObject);
                list.WriteLine(JsonSerializer.Serialize(new { pid=f.Pid,process=tracker.Name(f.Pid),fileObject=f.FileObject.ToString("X"),path },JsonStore.Options));
                Manifest.OpenFilesAtStart++;
            }
        }
        // Event times count from here.
        Manifest.StartedUtc=DateTimeOffset.UtcNow;
        _clock.Start();
        using var session=new TraceEventSession(SessionPrefix+Environment.ProcessId+"-"+Guid.NewGuid().ToString("N")[..8]) { StopOnDispose=true,BufferSizeMB=options.BufferMB };
        // The kernel provider must be enabled before anything touches session.Source (or EventsLost): the Source getter starts
        // the session, and TraceEvent then refuses the kernel provider ("must be enabled first and only once in a session").
        // Events logged before Process() wait in the session buffers.
        var keywords=KernelTraceEventParser.Keywords.Process|KernelTraceEventParser.Keywords.Thread|KernelTraceEventParser.Keywords.FileIO|KernelTraceEventParser.Keywords.FileIOInit|KernelTraceEventParser.Keywords.DiskFileIO|KernelTraceEventParser.Keywords.MemoryHardFaults|KernelTraceEventParser.Keywords.VAMap;
        if(options.CollectDisk) keywords|=KernelTraceEventParser.Keywords.DiskIO|KernelTraceEventParser.Keywords.DiskIOInit;
        try { session.EnableKernelProvider(keywords); }
        catch(Exception ex)
        {
            Manifest.State="Failed"; Manifest.EndedUtc=DateTimeOffset.UtcNow; Manifest.Warnings.Add("ETWセッションを開始できませんでした: "+ex.Message);
            JsonStore.Write(Path.Combine(output,"session.json"),Manifest);
            throw new IOException("ETWセッションを開始できませんでした。 "+ex.Message,ex);
        }
        void ReadLossCount()
        {
            try { Manifest.EtwEventsLost=Math.Max(Manifest.EtwEventsLost,session.EventsLost);Manifest.EtwLossCountAvailable=true; }
            catch(Exception ex) when(ex is ObjectDisposedException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException) { }
        }
        var channel=Channel.CreateBounded<IoEvent>(new BoundedChannelOptions(options.QueueCapacity) { SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait });
        var probes=Channel.CreateBounded<string>(new BoundedChannelOptions(4096) { SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait });
        var probed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending=new Dictionary<ulong,IoEvent>();
        var unhandled=new Dictionary<string,long>();
        var unresolvedBy=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
        var unresolvedSample=new List<object>();
        // FileKey (the file itself) → its path, from every event and name event that had one.
        var keyPaths=new Dictionary<ulong,string>();
        using var backgroundStop=new CancellationTokenSource();
        // The events go to numbered segments, compressed once closed (RecordLog); the sampler below hands them to the OS every
        // second and to the disk every 10 seconds, so a crash loses at most that much.
        using var log=new RecordLogWriter(output,RecordLog.Events);
        var writer=Task.Run(async()=>
        {
            await foreach(var e in channel.Reader.ReadAllAsync())
            {
                log.Write(JsonSerializer.Serialize(e,JsonStore.Options));
                Metrics.Add(e); Interlocked.Increment(ref _written);
            }
        });
        var processGate=new object();
        using var processLog=new StreamWriter(Path.Combine(output,"processes.jsonl"),false,new UTF8Encoding(false));
        void Proc(ProcessRecord r) { lock(processGate) processLog.WriteLine(JsonSerializer.Serialize(r,JsonStore.Options)); }
        var probeTask=Task.Run(async()=>
        {
            using var stream=new StreamWriter(Path.Combine(output,"sizes.jsonl"),false,new UTF8Encoding(false)) { AutoFlush=true };
            await foreach(var path in probes.Reader.ReadAllAsync())
            {
                var observation=new SizeObservation { Path=path,ObservedUs=NowUs };
                try
                {
                    var info=new FileInfo(path); observation.IsDirectory=(info.Attributes&FileAttributes.Directory)!=0; observation.Size=observation.IsDirectory?null:info.Length;
                    observation.Attributes=(uint)info.Attributes; observation.CreatedUtc=info.CreationTimeUtc; observation.LastWriteUtc=info.LastWriteTimeUtc;
                }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { observation.Error=ex.GetType().Name; }
                observation.ObservedUs=NowUs;
                await stream.WriteLineAsync(JsonSerializer.Serialize(observation,JsonStore.Options));
            }
        });
        bool rootWarned=false;
        void Checkpoint()
        {
            Manifest.EventsWritten=Interlocked.Read(ref _written); Manifest.QueueEventsDropped=Interlocked.Read(ref _dropped);
            Manifest.UnresolvedPaths=Interlocked.Read(ref _unresolved); Manifest.RootMatchedEvents=Interlocked.Read(ref _rootMatched);
            Manifest.CheckpointUtc=DateTimeOffset.UtcNow;
            lock(Manifest.Warnings) JsonStore.Write(Path.Combine(output,"session.json"),Manifest);
        }
        void Warn(string warning) { lock(Manifest.Warnings) Manifest.Warnings.Add(warning); progress?.Report(warning); }
        var memoryTask=Task.Run(async()=>
        {
            using var stream=new StreamWriter(Path.Combine(output,"memory.jsonl"),false,new UTF8Encoding(false)) { AutoFlush=true };
            using var csv=new StreamWriter(Path.Combine(output,"memory.csv"),false,new UTF8Encoding(true)) { AutoFlush=true };
            csv.WriteLine("time_us,total_bytes,available_bytes,commit_bytes,commit_limit_bytes,system_cache_bytes,standby_bytes,modified_bytes,cpu_percent,target_working_set_bytes,target_private_bytes,target_cpu_percent,network_received_bytes,network_sent_bytes");
            var sampler=new SystemSampler(); var described=new HashSet<int>();
            bool lowWarned=false; long tick=0;
            try
            {
                do
                {
                    var m=sampler.Sample(NowUs,tracker.TargetPids());
                    LatestMemory=m;
                    ReadLossCount();
                    if(!rootWarned && options.Mode!=CaptureMode.Process && NowUs>10_000_000 && RootMatchedEvents==0)
                    {
                        rootWarned=true;
                        Warn("記録開始から10秒間、対象フォルダに一致するI/Oがありません。ドライブ文字・UNC・デバイスパスの解決を確認してください（session.jsonのunresolvedDevicePrefixes）。");
                    }
                    await stream.WriteLineAsync(JsonSerializer.Serialize(m,JsonStore.Options));
                    await csv.WriteLineAsync($"{m.TimeUs},{m.TotalBytes},{m.AvailableBytes},{m.CommitBytes},{m.CommitLimitBytes},{m.SystemCacheBytes},{m.StandbyBytes},{m.ModifiedBytes},{m.CpuPercent},{m.Processes.Sum(p=>p.WorkingSetBytes)},{m.Processes.Sum(p=>p.PrivateBytes)},{m.Processes.Sum(p=>p.CpuPercent??0)},{m.Network.Sum(n=>n.ReceivedBytes)},{m.Network.Sum(n=>n.SentBytes)}");
                    // The program file and version of each target process, once.
                    foreach(var pid in tracker.TargetPids().Where(described.Add))
                        try
                        {
                            using var p=Process.GetProcessById(pid);
                            var image=p.MainModule?.FileName; var version=image is null?null:FileVersionInfo.GetVersionInfo(image);
                            Proc(new() { TimeUs=NowUs,Kind="Image",Pid=pid,Name=p.ProcessName,ImagePath=image,FileVersion=version?.FileVersion,ProductName=version?.ProductName,ProductVersion=version?.ProductVersion });
                        }
                        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException) { }
                    log.Flush(toDisk:tick%10==0);
                    lock(processGate) processLog.Flush();
                    if(tick%10==0) Checkpoint();
                    if(FreeBytes(output) is { } free)
                    {
                        if(free<StopFreeBytes)
                        {
                            Warn($"記録の保存先の空き容量が {StopFreeBytes>>30} GiB を下回ったため、記録を止めました（ここまでの記録は保存しています）。");
                            _stoppedForSpace=true; try { ReadLossCount(); session.Stop(); } catch(ObjectDisposedException) { }
                            break;
                        }
                        if(free<WarnFreeBytes && !lowWarned) { lowWarned=true; Warn($"記録の保存先の空き容量が {free/1073741824.0:N1} GiB です。{StopFreeBytes>>30} GiB を下回ると記録を止めます。"); }
                    }
                    tick++;
                    await Task.Delay(1000,backgroundStop.Token);
                } while(!backgroundStop.IsCancellationRequested);
            }
            catch(OperationCanceledException) { }
        });
        var lucidTask=LucidCollector.RunAsync(options,output,_clock,progress,backgroundStop.Token);
        Exception? failure=null;
        long Us(TraceEvent e)=>Math.Max(0,(long)((e.TimeStamp.ToUniversalTime()-Manifest.StartedUtc.UtcDateTime).TotalMilliseconds*1000));
        void Publish(IoEvent e)
        {
            if(!channel.Writer.TryWrite(e)) Interlocked.Increment(ref _dropped);
        }
        var renames=new RenameCorrelator(Publish);
        long lastUs=0;
        // Every finished or abandoned request goes through here so that renames wait for their destination.
        void Emit(IoEvent e) { if(e.Operation==IoOperation.Rename) renames.Completed(e,lastUs); else Publish(e); }
        var others=new OtherProcessCounter();
        IoEvent? Create(TraceEvent data,IoOperation op,ulong fileObject,string path,ulong fileKey=0,long size=0)
        {
            lastUs=Us(data);
            if(data.ProcessID==Environment.ProcessId && !options.IncludeOwnProcess) return null;
            path=Display(path ?? "",out bool rm);
            if(path.Length==0 && fileObject!=0 && openPaths.TryGetValue(fileObject,out var known)) { path=known; rm=roots.Match(known,known,out _); }
            // The file itself may have been named by another handle's event or a name event: its key gives the path.
            if(path.Length==0 && fileKey!=0 && keyPaths.TryGetValue(fileKey,out var named)) { path=named; rm=roots.Match(named,named,out _); }
            else if(path.Length>0 && fileKey!=0 && keyPaths.Count<KeyPathLimit) keyPaths[fileKey]=path;
            if(fileKey!=0) renames.Observe(fileKey,path,lastUs);
            if(path.Length>0 && PathPolicy.IsUnder(path,output)) return null;
            bool pm=tracker.Matches(data.ProcessID),fm=false;
            if(options.Mode!=CaptureMode.Folder)
            {
                if(pm) { if(fileKey!=0) targetKeys.Add(fileKey); if(fileObject!=0) targetObjects.Add(fileObject); }
                else fm=fileKey!=0 && targetKeys.Contains(fileKey) || fileObject!=0 && targetObjects.Contains(fileObject);
            }
            bool match=options.Mode switch { CaptureMode.Process=>pm||fm,CaptureMode.Folder=>rm,_=>pm||rm||fm };
            if(path.Length==0)
            {
                Interlocked.Increment(ref _unresolved);
                var name=tracker.Name(data.ProcessID);
                if(unresolvedBy.Count<4096) unresolvedBy[name]=unresolvedBy.GetValueOrDefault(name)+1;
                // A sample of what stayed unnamed (unresolved-sample.jsonl), to tell later which files and processes they were.
                if(unresolvedSample.Count<UnresolvedSampleSize)
                    unresolvedSample.Add(new { timeUs=lastUs,operation=op,pid=data.ProcessID,process=name,fileObject=fileObject.ToString("X"),fileKey=fileKey!=0?fileKey.ToString("X"):null,matchesProcess=pm });
            }
            if(!match)
            {
                // Counted without its path, so a busy program that is not a target shows up afterwards (OtherProcessIo).
                if(op is not (IoOperation.HardFault or IoOperation.DiskRead or IoOperation.DiskWrite or IoOperation.MapView or IoOperation.UnmapView))
                    others.Add(tracker.Name(data.ProcessID),path,op,size);
                return null;
            }
            if(rm) Interlocked.Increment(ref _rootMatched);
            if(options.ProbeSizes && path.Length>0 && probed.Add(path))
                if(!probes.Writer.TryWrite(path)) progress?.Report("サイズ照会の待ち行列が満杯です。一部のサイズは不明になります。");
            return new() { Sequence=++_sequence,StartUs=lastUs,Operation=op,ProcessId=data.ProcessID,ThreadId=data.ThreadID,ProcessName=tracker.Name(data.ProcessID),Path=path,FileObject=fileObject.ToString("X"),
                FileKey=fileKey!=0?fileKey.ToString("X"):null,MatchesProcess=pm,MatchesRoot=rm,MatchesFile=fm };
        }
        void Begin(ulong irp,IoEvent? e)
        {
            if(e is null) return;
            if(irp==0) { e.Note="No IRP correlation"; Emit(e); return; }
            if(pending.Remove(irp,out var prior))
            {
                if(prior.Operation==e.Operation && prior.Path==e.Path) { e.Sequence=prior.Sequence;e.StartUs=Math.Min(prior.StartUs,e.StartUs);e.DestinationPath??=prior.DestinationPath; renames.Forget(prior); }
                else { prior.Note="IRP reused before completion"; Emit(prior); }
            }
            if(pending.Count>=options.QueueCapacity)
            {
                // Do not grow without bound if OperationEnd is absent on this filesystem.
                foreach(var old in pending.Values) { old.Note="Pending operation limit"; Emit(old); }
                pending.Clear();
            }
            pending[irp]=e;
        }
        void Simple(FileIOSimpleOpTraceData d,IoOperation op)
        {
            Begin(d.IrpPtr,Create(d,op,d.FileObject,d.FileName,d.FileKey));
            if(op==IoOperation.Close) { openPaths.Remove(d.FileObject); targetObjects.Remove(d.FileObject); }
        }
        void Info(FileIOInfoTraceData d,IoOperation op)
        {
            // Kernel FileIo logs dispositions and renames as their own opcodes (Delete=70, Rename=71), not as SetInfo.
            if(op==IoOperation.SetInfo) op=d.InfoClass switch { 20=>IoOperation.SetEndOfFile,19=>IoOperation.SetAllocation,13 or 64 when d.ExtraInfo!=0=>IoOperation.Delete,10 or 65=>IoOperation.Rename,_=>IoOperation.SetInfo };
            var e=Create(d,op,d.FileObject,d.FileName,d.FileKey);
            if(e is null) return;
            e.InfoClass=d.InfoClass; e.ExtraInfo=d.ExtraInfo;
            if(op is IoOperation.SetEndOfFile or IoOperation.SetAllocation) e.Size=unchecked((long)d.ExtraInfo);
            if(op==IoOperation.Rename) renames.Started(d.FileKey,e);
            Begin(d.IrpPtr,e);
        }
        void ReadWrite(FileIOReadWriteTraceData d,IoOperation op)
        {
            var e=Create(d,op,d.FileObject,d.FileName,d.FileKey,(uint)d.IoSize);
            if(e is not null) { e.Offset=d.Offset; e.Size=(uint)d.IoSize; e.IoFlags=unchecked((uint)d.IoFlags); }
            Begin(d.IrpPtr,e);
        }
        void Folder(FileIODirEnumTraceData d,IoOperation op)
        {
            var e=Create(d,op,d.FileObject,d.DirectoryName,d.FileKey);
            if(e is not null) { e.IsDirectory=true; e.InfoClass=d.InfoClass; e.Size=d.Length; e.SearchPattern=d.FileName; if(op==IoOperation.DirectoryEnumerate) e.Note="Enumeration results are not included in ETW"; }
            Begin(d.IrpPtr,e);
        }
        void View(MapFileTraceData d,IoOperation op,string? note=null)
        {
            var e=Create(d,op,0,d.FileName,d.FileKey);
            if(e is not null) { e.EndUs=e.StartUs; e.Offset=d.ByteOffset; e.Size=unchecked((long)d.ViewSize); e.Note=note; Publish(e); }
        }
        var kernel=session.Source.Kernel;
        kernel.ProcessStart+=d=> { tracker.Started(d.ProcessID,d.ImageFileName,d.ParentID); Proc(new() { TimeUs=Us(d),Kind="Start",Pid=d.ProcessID,ParentPid=d.ParentID,Name=d.ImageFileName,CommandLine=d.CommandLine,SessionId=d.SessionID }); };
        kernel.ProcessDCStart+=d=> { tracker.Running(d.ProcessID,d.ImageFileName,d.ParentID); Proc(new() { TimeUs=Us(d),Kind="Running",Pid=d.ProcessID,ParentPid=d.ParentID,Name=d.ImageFileName,CommandLine=d.CommandLine,SessionId=d.SessionID }); };
        kernel.ProcessStop+=d=> { Proc(new() { TimeUs=Us(d),Kind="Stop",Pid=d.ProcessID,ParentPid=d.ParentID,Name=d.ImageFileName,ExitStatus=d.ExitStatus }); tracker.Remove(d.ProcessID); };
        void Name(FileIONameTraceData d)
        {
            if(d.FileKey==0) return;
            var named=Display(d.FileName ?? "",out _);
            renames.Observe(d.FileKey,named,Us(d));
            if(named.Length>0 && keyPaths.Count<KeyPathLimit) keyPaths[d.FileKey]=named;
        }
        kernel.FileIOName+=Name; kernel.FileIOFileCreate+=Name; kernel.FileIOFileRundown+=Name;
        // The file is gone from memory: its key may be reused for another file.
        kernel.FileIOFileDelete+=d=> { targetKeys.Remove(d.FileKey); keyPaths.Remove(d.FileKey); };
        kernel.FileIOCreate+=d=>
        {
            if(!string.IsNullOrEmpty(d.FileName)) openPaths[d.FileObject]=Display(d.FileName,out _);
            var e=Create(d,IoOperation.Open,d.FileObject,d.FileName);
            if(e is not null) { e.CreateDisposition=(int)d.CreateDisposition; e.CreateOptions=(uint)d.CreateOptions; e.ShareAccess=(uint)d.ShareAccess; e.IsDirectory=((uint)d.CreateOptions&1)!=0 || (d.FileAttributes&FileAttributes.Directory)!=0; }
            Begin(d.IrpPtr,e);
        };
        kernel.FileIORead+=d=>ReadWrite(d,IoOperation.Read);
        kernel.FileIOWrite+=d=>ReadWrite(d,IoOperation.Write);
        kernel.FileIOClose+=d=>Simple(d,IoOperation.Close);
        kernel.FileIOCleanup+=d=>Simple(d,IoOperation.Cleanup);
        kernel.FileIOFlush+=d=>Simple(d,IoOperation.Flush);
        kernel.FileIOSetInfo+=d=>Info(d,IoOperation.SetInfo);
        kernel.FileIODelete+=d=>Info(d,IoOperation.Delete);
        kernel.FileIORename+=d=>Info(d,IoOperation.Rename);
        kernel.FileIOQueryInfo+=d=>Info(d,IoOperation.QueryInfo);
        kernel.FileIOFSControl+=d=>Info(d,IoOperation.FsControl);
        kernel.FileIODirEnum+=d=>Folder(d,IoOperation.DirectoryEnumerate);
        kernel.FileIODirNotify+=d=>Folder(d,IoOperation.DirectoryNotify);
        kernel.FileIOMapFile+=d=>View(d,IoOperation.MapView);
        kernel.FileIOMapFileDCStart+=d=>View(d,IoOperation.MapView,"Mapped before the recording started");
        kernel.FileIOUnmapFile+=d=>View(d,IoOperation.UnmapView);
        kernel.FileIOOperationEnd+=d=>
        {
            lastUs=Us(d);
            if(!pending.Remove(d.IrpPtr,out var e)) return;
            e.EndUs=lastUs; e.NtStatus=unchecked((uint)d.NtStatus);
            if(e.Operation is IoOperation.Read or IoOperation.Write) e.Transferred=e.Failed?0:Math.Min(e.Size,(long)Math.Min(d.ExtraInfo,(ulong)long.MaxValue));
            if(e.Operation==IoOperation.Open) e.ExtraInfo=d.ExtraInfo;
            Emit(e);
        };
        kernel.MemoryHardFault+=d=>
        {
            var e=Create(d,IoOperation.HardFault,d.FileKey,d.FileName,d.FileKey);
            if(e is not null) { e.EndUs=Us(d); e.Offset=d.ReadOffset; e.Size=d.ByteCount; Publish(e); }
        };
        // Opcodes this TraceEvent version does not parse (for example newer path-operation events) are counted for diagnosis.
        session.Source.UnhandledEvents+=d=> { if(d.TaskGuid==FileIoTaskGuid) { var key=((int)d.Opcode).ToString(); unhandled[key]=unhandled.GetValueOrDefault(key)+1; } };
        if(options.CollectDisk)
        {
            void Disk(DiskIOTraceData d,IoOperation op)
            {
                var e=Create(d,op,d.FileKey,d.FileName,d.FileKey);
                if(e is not null)
                {
                    e.EndUs=Us(d); e.StartUs=Math.Max(0,e.EndUs.Value-(long)(d.ElapsedTimeMSec*1000)); e.Offset=d.ByteOffset; e.Size=(uint)d.TransferSize;
                    e.DiskNumber=d.DiskNumber; e.IoFlags=unchecked((uint)d.IrpFlags); e.Note="Physical disk offset; never replayed as file offset"; Publish(e);
                }
            }
            kernel.DiskIORead+=d=>Disk(d,IoOperation.DiskRead); kernel.DiskIOWrite+=d=>Disk(d,IoOperation.DiskWrite);
        }
        try
        {
            using var cancelRegistration=token.Register(()=> { try { ReadLossCount();session.Stop(); } catch(ObjectDisposedException) { } });
            progress?.Report("ETW計測を開始しました。対象アプリで操作してください。"); ready?.Invoke();
            // The application starts only now, so its start-up is recorded; its events wait in the session buffers meanwhile.
            if(options.Launch is { } app)
                try { Manifest.LaunchedProcessId=AppLauncher.Start(app); progress?.Report($"{app.Name} を起動しました（PID {Manifest.LaunchedProcessId}）。作業が終わったら「停止して保存」を押してください。"); }
                catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
                { Warn($"{app.Name} を起動できませんでした（{ex.Message}）。記録は続いています。いつもの方法で {app.Name} を起動してください。"); }
            var processing=Task.Run(()=>session.Source.Process());
            var completed=await Task.WhenAny(processing,writer,probeTask,memoryTask);
            // The free-space guard ends the sampler and stops the session: the recording finishes normally.
            if(completed==memoryTask && _stoppedForSpace) completed=processing;
            if(completed!=processing)
            {
                try { await completed; throw new IOException("記録のバックグラウンド処理が停止しました。"); }
                finally { session.Stop(); await processing; }
            }
            await processing;
            if(!token.IsCancellationRequested && !_stoppedForSpace) throw new IOException("ETWセッションが予期せず停止しました。");
        }
        catch(Exception ex) { failure=ex; }
        finally
        {
            if(!Manifest.EtwLossCountAvailable) Manifest.Warnings.Add("ETW欠落数を取得できませんでした。欠落0を確認した記録ではありません。");
            // A change-notification request waits for a change; one still waiting at the end is not a missing completion.
            Manifest.IncompleteOperations=pending.Values.Count(e=>e.Operation!=IoOperation.DirectoryNotify);
            foreach(var e in pending.Values) { e.Note=e.Operation==IoOperation.DirectoryNotify?"Still waiting for a change at stop":"No completion before stop"; Emit(e); }
            pending.Clear(); renames.Flush(); channel.Writer.TryComplete(); probes.Writer.TryComplete(); backgroundStop.Cancel();
            try { await Task.WhenAll(writer,probeTask,memoryTask,lucidTask,environment); } catch(Exception ex) { failure??=ex; }
            log.Dispose();
            lock(processGate) processLog.Flush();
            if(unresolvedSample.Count>0)
                try { File.WriteAllLines(Path.Combine(output,"unresolved-sample.jsonl"),unresolvedSample.Select(s=>JsonSerializer.Serialize(s,JsonStore.Options))); }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
            Manifest.EndedUtc=DateTimeOffset.UtcNow; Manifest.EventsWritten=_written; Manifest.QueueEventsDropped=_dropped; Manifest.UnresolvedPaths=_unresolved;
            Manifest.RootMatchedEvents=_rootMatched; Manifest.UnhandledFileIoOpcodes=unhandled; Manifest.CheckpointUtc=DateTimeOffset.UtcNow;
            Manifest.UnresolvedPathsByProcess=unresolvedBy.OrderByDescending(p=>p.Value).Take(50).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);
            Manifest.UnresolvedDevicePrefixes=devicePrefixes.OrderByDescending(p=>p.Value).Take(20).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);
            Manifest.UnrecordedEvents=others.Events; Manifest.OtherProcessIo=others.Top();
            Manifest.State=failure is null?"Complete":"Failed";
            if(failure is not null) Manifest.Warnings.Add(failure.Message);
            if(_dropped>0 || Manifest.EtwEventsLost>0) Manifest.Warnings.Add("イベントが欠落しています。リプレイの完全性は保証されません。");
            if(options.Mode!=CaptureMode.Process && _rootMatched==0 && !rootWarned) Manifest.Warnings.Add("対象フォルダに一致するI/Oが記録されませんでした。パスの解決を確認してください。");
            JsonStore.Write(Path.Combine(output,"session.json"),Manifest);
            session.Stop();
        }
        if(failure is not null) throw new IOException("計測に失敗しました。保存済みの記録は残しています。 "+failure.Message,failure);
        progress?.Report("計測停止。レポートを作成しています…");
        return await Task.Run(()=>SessionAnalyzer.Export(output,progress));
    }
    // Free space for the record's drive (null for a network path or when it cannot be read).
    private static long? FreeBytes(string folder)
    {
        try { return Path.GetPathRoot(folder) is { Length: 3 } root ? new DriveInfo(root).AvailableFreeSpace : null; }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    private static string DevicePrefix(string path)
    {
        int second=path.IndexOf('\\',@"\Device\".Length);
        return second<0?path:path[..second];
    }
}
