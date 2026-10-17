using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace IoRecorder.Core;

public struct DeterministicData
{
    private ulong _state;
    public DeterministicData(int seed,string identity)
    { _state=BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(seed+":"+identity))); }
    public void Fill(Span<byte> bytes)
    {
        int i=0;
        while(i+8<=bytes.Length) { BinaryPrimitives.WriteUInt64LittleEndian(bytes[i..],Next()); i+=8; }
        if(i<bytes.Length) { ulong value=Next(); while(i<bytes.Length) { bytes[i++]=(byte)value; value>>=8; } }
    }
    private const ulong Gamma=0x9E3779B97F4A7C15UL;
    private ulong Next()
    {
        ulong z=(_state+=Gamma);
        z=(z^(z>>30))*0xBF58476D1CE4E5B9UL; z=(z^(z>>27))*0x94D049BB133111EBUL;
        return z^(z>>31);
    }
    // Bytes [offset, offset+length) of the stream Fill produces from the start in chunks that are multiples of 8 bytes
    // (as the dataset generator writes). SplitMix64 can jump: the k-th value only depends on state + k*Gamma.
    public static void FillAt(int seed,string identity,long offset,Span<byte> bytes)
    {
        var data=new DeterministicData(seed,identity);
        data._state+=unchecked((ulong)(offset/8)*Gamma);
        int i=0,skip=(int)(offset%8);
        if(skip>0)
        {
            ulong value=data.Next()>>(skip*8);
            for(int k=skip;k<8 && i<bytes.Length;k++) { bytes[i++]=(byte)value; value>>=8; }
        }
        while(i+8<=bytes.Length) { BinaryPrimitives.WriteUInt64LittleEndian(bytes[i..],data.Next()); i+=8; }
        if(i<bytes.Length) { ulong value=data.Next(); while(i<bytes.Length) { bytes[i++]=(byte)value; value>>=8; } }
    }
}

public sealed class ResetSummary
{
    public int DeletedFiles { get; set; }
    public int DeletedDirectories { get; set; }
    public int RecreatedFiles { get; set; }
    public int ResizedFiles { get; set; }
    public long RewrittenBytes { get; set; }
    // Entries left as they are because they would leave the dummy or pass through a link inside it.
    public int SkippedPaths { get; set; }
}

public static class DatasetBuilder
{
    public const string Marker=".iorecorder-dataset.json";
    // processOnly / rootOnly: only the files the replay with the same scope will touch are generated.
    public static async Task<DatasetManifest> GenerateAsync(string session,string destination,int seed=42,IProgress<string>? progress=null,CancellationToken token=default,IProgress<WorkProgress>? work=null,bool processOnly=false,bool rootOnly=false)
    {
        work?.Report(new("生成するファイルを集計中",0,0,"記録を読み込んでいます"));
        session=Path.GetFullPath(session); destination=Path.GetFullPath(destination);
        PathPolicy.RejectReparsePoint(destination);
        if(Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("ダミー生成先は空のフォルダにしてください。再試験には新しいフォルダを使います。");
        var source=JsonStore.Read<SessionManifest>(Path.Combine(session,"session.json"));
        var analysis=SessionAnalyzer.Load(session);
        var m=new DatasetManifest { SessionId=source.Id,Seed=seed,Session=session,Scope=processOnly?"ProcessOnly":rootOnly?"RootOnly":"All" };
        // Paths of the successful operations the replay with this scope will run, and names the record looked up without finding
        // them: the replay looks them up again, so they get a dummy path, but nothing is created there.
        var wanted=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinations=new List<string>();
        var lookups=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var e in RecordLog.Read<IoEvent>(session, RecordLog.Events))
        {
            if(!e.IsFileIo || e.ByOtherProcess || processOnly&&!e.MatchesProcess || rootOnly&&!e.MatchesRoot) continue;
            if(e.Failed)
            {
                if(e.Operation==IoOperation.Open && e.NtStatus is 0xC0000034 or 0xC000003A && ReplayExecutor.ReplaysFailure(e) && !string.IsNullOrEmpty(e.Path)) lookups.Add(e.Path);
                continue;
            }
            if(!string.IsNullOrEmpty(e.Path)) wanted.Add(e.Path);
            if(!string.IsNullOrEmpty(e.DestinationPath) && wanted.Add(e.DestinationPath)) destinations.Add(e.DestinationPath);
        }
        // A path that cannot be made into a safe dataset path is left out, and its operations are skipped by the replay.
        var excluded=new List<string>();
        void Map(string path)
        {
            try { m.Paths[path]=PathPolicy.DatasetRelative(path); }
            catch(InvalidDataException ex) { excluded.Add(ex.Message); }
        }
        var files=analysis.FileManifest.Where(f=>!string.IsNullOrEmpty(f.Path) && wanted.Contains(f.Path)).ToList();
        foreach(var f in files) Map(f.Path);
        foreach(var path in destinations) if(!m.Paths.ContainsKey(path)) Map(path);
        // Never create a file where another replayed path needs a directory.
        var parents=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var relative in m.Paths.Values)
            for(var parent=Path.GetDirectoryName(relative);!string.IsNullOrEmpty(parent) && parents.Add(parent);parent=Path.GetDirectoryName(parent)) { }
        // After the folders are decided: a lookup below a file (such as "a.psd\x") must not turn that file into a folder.
        foreach(var path in lookups) if(!m.Paths.ContainsKey(path)) Map(path);
        // Spellings of one path (a trailing separator, \\?\, letter case) become the same dataset path: they are one file or folder.
        // The earliest access decides whether it existed; any spelling seen as a folder makes it a folder.
        foreach(var g in files.Where(f=>m.Paths.ContainsKey(f.Path)).GroupBy(f=>m.Paths[f.Path],StringComparer.OrdinalIgnoreCase))
        {
            if(!g.MinBy(f=>f.FirstUs)!.InitiallyExists) continue;
            if(parents.Contains(g.Key) || g.Any(f=>f.IsDirectory || f.Path.EndsWith('\\') || f.Path.EndsWith('/'))) m.Directories.Add(g.Key);
            else m.InitialSizes[g.Key]=g.Max(f=>f.SuggestedInitialSize);
        }
        long required=m.InitialSizes.Values.Aggregate(0L,(sum,size)=>checked(sum+size));
        var driveRoot=Path.GetPathRoot(destination);
        if(driveRoot?.Length==3)
        {
            var drive=new DriveInfo(driveRoot);
            if(required>drive.AvailableFreeSpace) throw new IOException($"生成に必要な容量 {required/1073741824.0:N1} GiB が空き容量を超えています。");
        }
        Directory.CreateDirectory(destination);
        JsonStore.Write(Path.Combine(destination,Marker),m);
        // A file or folder the storage refuses (a name it does not accept, a path too long) is left out like an unconvertible
        // path: the replay skips its operations and the rest of the dummy is still made. A full disk still stops generation.
        void Drop(string relative,Exception ex)
        {
            excluded.Add($"作成できませんでした（{ex.Message.TrimEnd()}）: {relative}");
            foreach(var original in m.Paths.Where(p=>p.Value.Equals(relative,StringComparison.OrdinalIgnoreCase)).Select(p=>p.Key).ToList()) m.Paths.Remove(original);
        }
        static bool Refused(Exception ex)=>ex is UnauthorizedAccessException || ex is IOException && ex.HResult is not (unchecked((int)0x80070070) or unchecked((int)0x80070027));
        byte[] buffer=ArrayPool<byte>.Shared.Rent(1024*1024);
        long completed=0; int fileIndex=0,fileCount=m.InitialSizes.Count;
        var clock=System.Diagnostics.Stopwatch.StartNew(); double lastUpdate=-1;
        try
        {
            foreach(var relative in m.Directories.ToList())
            {
                token.ThrowIfCancellationRequested();
                try { Directory.CreateDirectory(PathPolicy.CombineInside(destination,relative)); }
                catch(Exception ex) when(Refused(ex)) { m.Directories.Remove(relative); Drop(relative,ex); }
            }
            foreach(var (relative,length) in m.InitialSizes.ToList())
            {
                token.ThrowIfCancellationRequested();
                // The destination was empty and checked for links above; everything below it is created here.
                var path=PathPolicy.CombineInside(destination,relative); bool created=false;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var random=new DeterministicData(seed,relative);
                    await using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read,1024*1024,FileOptions.Asynchronous|FileOptions.SequentialScan);
                    created=true;
                    long remaining=length;
                    while(remaining>0)
                    {
                        token.ThrowIfCancellationRequested();
                        int n=(int)Math.Min(remaining,buffer.Length);
                        random.Fill(buffer.AsSpan(0,n)); await stream.WriteAsync(buffer.AsMemory(0,n),token);
                        remaining-=n; completed+=n;
                        if(clock.Elapsed.TotalSeconds-lastUpdate>=.25)
                        {
                            progress?.Report($"ダミー生成 {fileIndex+1:N0}/{fileCount:N0} ファイル · {completed/1048576.0:N1}/{required/1048576.0:N1} MiB");
                            work?.Report(new("ダミーファイルを生成中",completed,required,$"{fileIndex+1:N0} / {fileCount:N0} ファイル · {completed/1048576.0:N0} / {required/1048576.0:N0} MiB · {completed/1048576.0/Math.Max(.001,clock.Elapsed.TotalSeconds):N0} MiB/s · {relative}"));
                            lastUpdate=clock.Elapsed.TotalSeconds;
                        }
                    }
                    await stream.FlushAsync(token); fileIndex++;
                }
                catch(Exception ex) when(Refused(ex))
                {
                    m.InitialSizes.Remove(relative); Drop(relative,ex);
                    if(created) try { File.Delete(path); } catch(IOException) { }
                }
            }
            m.ExcludedPaths=excluded.Count;
            if(excluded.Count>0)
            {
                m.Notes.Add($"ダミーの対象外: {excluded.Count:N0} パス。再生では、これらへの操作を対象外（スキップ）として数えます。"+(excluded.Count>20?"先頭の20件:":""));
                m.Notes.AddRange(excluded.Take(20));
            }
            if(m.Scope!="All") m.Notes.Add(m.Scope=="ProcessOnly"?"指定プロセスに一致したI/Oが使うファイルだけを作りました。":"指定フォルダに一致したI/Oが使うファイルだけを作りました。");
            m.State="Ready";
            m.Notes.Add("疑似乱数の非スパースファイル。圧縮されにくいデータ条件です。");
            m.Notes.Add("初期サイズは非同期照会値またはRead範囲からの推定です。LucidLinkへの初期アップロードとキャッシュ調整は再生前に行ってください。");
            JsonStore.Write(Path.Combine(destination,Marker),m);
            progress?.Report($"ダミー生成完了: {fileIndex:N0} ファイル / {completed/1048576.0:N1} MiB");
            work?.Report(new("ダミー生成完了",required,Math.Max(1,required),$"{fileIndex:N0} ファイル / {completed/1048576.0:N0} MiB · {clock.Elapsed.TotalSeconds:N1} 秒"));
            return m;
        }
        catch
        {
            m.State="Incomplete"; JsonStore.Write(Path.Combine(destination,Marker),m); throw;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    // Brings a used dataset back to the state right after generation, so the same test can run again without regenerating
    // (and re-uploading) everything: files and folders created by the runs are deleted, missing files are recreated,
    // sizes are restored, and only the ranges the runs wrote are rewritten with the original deterministic content.
    public static async Task<ResetSummary> ResetAsync(string dataset,IProgress<string>? progress=null,CancellationToken token=default,IProgress<WorkProgress>? work=null)
    {
        dataset=Path.GetFullPath(dataset); PathPolicy.RejectReparsePoint(dataset);
        string marker=Path.Combine(dataset,Marker);
        var m=JsonStore.Read<DatasetManifest>(marker);
        if(m.State is "Preparing" or "Incomplete") throw new InvalidOperationException("生成が完了していないダミーは戻せません。ダミーを生成し直してください。");
        var summary=new ResetSummary();
        // Entries that would leave the dummy or pass through a link inside it are not restored (nothing is written through a link).
        var unsafePaths=PathPolicy.UnsafeInside(dataset,m.InitialSizes.Keys.Concat(m.Directories));
        summary.SkippedPaths=unsafePaths.Count;
        var initial=m.InitialSizes.Where(p=>!unsafePaths.Contains(p.Key)).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);
        // Links inside the dummy are neither entered nor deleted.
        var walk=new EnumerationOptions { RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint };
        work?.Report(new("書き換えられた範囲を調べています",0,0,"実行の結果を読み込んでいます"));
        // 1. What the runs changed in the files that existed after generation.
        var dirty=new Dictionary<string,List<(long Start,long End)>>(StringComparer.OrdinalIgnoreCase);
        var whole=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Mark(string relative,long start,long end)
        {
            end=Math.Min(end,initial[relative]); if(end<=start) return;
            if(!dirty.TryGetValue(relative,out var list)) dirty[relative]=list=[];
            list.Add((start,end));
        }
        // A run's results are compressed after it ends (<run>.gz).
        var runs=m.Runs.Where(r=>File.Exists(r) || File.Exists(r+".gz")).ToList();
        // A run that was cut off (the tool or the PC stopped while it ran) may have sent requests its results do not list
        // yet, so every write the record could have replayed is rewritten.
        bool cutOff=m.State=="Replaying";
        if(runs.Count>0 || cutOff)
        {
            if(m.Session is null || !RecordLog.Exists(m.Session,RecordLog.Events))
                throw new FileNotFoundException("このダミーを作った記録（またはベンチマーク）が見つからないため、書き換えた範囲が分かりません。ダミーを生成し直してください。");
            var touched=new HashSet<long>();
            foreach(var run in runs)
                foreach(var o in JsonStore.Lines<ReplayOutcome>(run,tornTail:true))
                    if(o.Status is "Executed" or "Failed" && o.Operation is IoOperation.Write or IoOperation.SetEndOfFile or IoOperation.Open) touched.Add(o.Sequence);
            foreach(var e in RecordLog.Read<IoEvent>(m.Session, RecordLog.Events))
            {
                token.ThrowIfCancellationRequested();
                if(!cutOff && !touched.Contains(e.Sequence) || e.Failed || !m.Paths.TryGetValue(e.Path,out var relative) || !initial.ContainsKey(relative)) continue;
                if(e.Operation==IoOperation.Write) Mark(relative,e.Offset,e.Offset+e.Size);
                else if(e.Operation==IoOperation.SetEndOfFile) Mark(relative,e.Size,long.MaxValue);
                else if(e.CreateDisposition is 0 or 4 or 5) whole.Add(relative); // supersede / overwrite: the file was emptied
            }
        }
        // 2. Delete what the runs created: anything that is not an initial file or folder.
        var keepDirectories=new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.TrimEndingDirectorySeparator(dataset) };
        foreach(var relative in m.Directories.Where(d=>!unsafePaths.Contains(d)).Concat(initial.Keys.Select(k=>Path.GetDirectoryName(k)??"")))
            for(var d=relative;!string.IsNullOrEmpty(d);d=Path.GetDirectoryName(d))
                if(PathPolicy.TryCombineInside(dataset,d,out var full)) keepDirectories.Add(full);
        var existing=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in new DirectoryInfo(dataset).EnumerateFiles("*",walk))
        {
            token.ThrowIfCancellationRequested();
            string relative=Path.GetRelativePath(dataset,file.FullName);
            if(relative.Equals(Marker,StringComparison.OrdinalIgnoreCase)) continue;
            if(initial.ContainsKey(relative)) { existing[relative]=file.Length; continue; }
            file.Attributes=FileAttributes.Normal; file.Delete(); summary.DeletedFiles++;
        }
        foreach(var directory in Directory.EnumerateDirectories(dataset,"*",walk).OrderByDescending(d=>d.Length))
            if(!keepDirectories.Contains(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) { Directory.Delete(directory); summary.DeletedDirectories++; }
        foreach(var directory in keepDirectories) Directory.CreateDirectory(directory);
        // 3. Restore sizes and rewrite changed ranges; missing or emptied files are rewritten whole.
        foreach(var (relative,length) in initial)
        {
            if(!existing.TryGetValue(relative,out var current)) { whole.Add(relative); summary.RecreatedFiles++; continue; }
            if(current!=length)
            {
                summary.ResizedFiles++;
                using(var handle=File.OpenHandle(PathPolicy.CombineInside(dataset,relative),FileMode.Open,FileAccess.ReadWrite)) RandomAccess.SetLength(handle,length);
                if(current<length) Mark(relative,current,length);
            }
        }
        var plan=initial.Keys.Select(relative=>(Relative:relative,Ranges:whole.Contains(relative)?new List<(long Start,long End)> { (0L,initial[relative]) }:Merge(dirty.GetValueOrDefault(relative)))).Where(p=>p.Ranges.Count>0).ToList();
        long total=plan.Sum(p=>p.Ranges.Sum(r=>r.End-r.Start)),done=0;
        byte[] buffer=ArrayPool<byte>.Shared.Rent(1024*1024);
        try
        {
            foreach(var (relative,ranges) in plan)
            {
                string path=PathPolicy.CombineInside(dataset,relative);
                using var handle=File.OpenHandle(path,FileMode.OpenOrCreate,FileAccess.ReadWrite);
                if(RandomAccess.GetLength(handle)!=initial[relative]) RandomAccess.SetLength(handle,initial[relative]);
                foreach(var (start,end) in ranges)
                    for(long offset=start;offset<end;)
                    {
                        token.ThrowIfCancellationRequested();
                        int n=(int)Math.Min(end-offset,buffer.Length);
                        DeterministicData.FillAt(m.Seed,relative,offset,buffer.AsSpan(0,n));
                        await RandomAccess.WriteAsync(handle,buffer.AsMemory(0,n),offset,token);
                        offset+=n; done+=n;
                        work?.Report(new("書き換えられた範囲を元に戻しています",done,Math.Max(1,total),$"{done/1048576.0:N1} / {total/1048576.0:N1} MiB · {relative}"));
                    }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        summary.RewrittenBytes=total;
        m.State="Ready"; m.Runs.Clear();
        m.Notes.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 初期状態に戻しました（削除 {summary.DeletedFiles} ファイル、作り直し {summary.RecreatedFiles}、サイズ復元 {summary.ResizedFiles}、書き戻し {total:N0} bytes"
            +(summary.SkippedPaths>0?$"、リンクを経由するなどで戻さなかったパス {summary.SkippedPaths}":"")+"）。");
        JsonStore.Write(marker,m);
        string message=$"初期状態に戻しました: 削除 {summary.DeletedFiles} ファイル / 作り直し {summary.RecreatedFiles} / 書き戻し {total/1048576.0:N1} MiB";
        progress?.Report(message); work?.Report(new("初期状態に戻しました",1,1,message));
        return summary;
    }
    private static List<(long Start,long End)> Merge(List<(long Start,long End)>? ranges)
    {
        var merged=new List<(long Start,long End)>();
        if(ranges is null) return merged;
        foreach(var r in ranges.OrderBy(r=>r.Start))
            if(merged.Count>0 && r.Start<=merged[^1].End) merged[^1]=(merged[^1].Start,Math.Max(merged[^1].End,r.End));
            else merged.Add(r);
        return merged;
    }
}
