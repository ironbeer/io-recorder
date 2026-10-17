using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IoRecorder.Core;

public enum BenchOperation { Read, Write }
public enum BenchPattern { Sequential, Random, WholeFile }
public enum BenchFileOrder { Random, Sequential }

// A seed is any text: a whole number is used as it is, other text is turned into a number (same text, same I/O list).
public static class Seeds
{
    // Empty, or only blanks and symbols (no letter or digit), means "no seed given".
    public static string? Normalize(string? text)
    {
        var trimmed=(text??"").Trim();
        return trimmed.Any(char.IsLetterOrDigit)?trimmed:null;
    }
    public static int Value(string seed)=>int.TryParse(seed,NumberStyles.Integer,CultureInfo.InvariantCulture,out var number)
        ?number:BitConverter.ToInt32(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
    // For folder names: seed42, or seed-<text>.
    public static string Tag(string seed)=>int.TryParse(seed,NumberStyles.Integer,CultureInfo.InvariantCulture,out _)?"seed"+seed:"seed-"+seed;
}
// Reads "seed": 42 as well as "seed": "LucidLink試験A"; numbers are written back as numbers.
public sealed class SeedTextConverter : System.Text.Json.Serialization.JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>reader.TokenType switch
    {
        JsonTokenType.Number=>reader.TryGetInt64(out var n)?n.ToString(CultureInfo.InvariantCulture):reader.GetDouble().ToString(CultureInfo.InvariantCulture),
        JsonTokenType.String=>reader.GetString()??"",
        _=>throw new JsonException("seed は数値か文字列で指定してください。")
    };
    public override void Write(Utf8JsonWriter writer,string value,JsonSerializerOptions options)
    { if(long.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out var n)) writer.WriteNumberValue(n); else writer.WriteStringValue(value); }
}

public sealed class BenchmarkConfig
{
    public string Label { get; set; } = "Synthetic benchmark";
    [System.Text.Json.Serialization.JsonConverter(typeof(SeedTextConverter))]
    public string Seed { get; set; } = "42";
    public int Threads { get; set; } = 8;
    // The amount read and written by the whole plan (4GiB when neither this nor duration is given).
    public string? TotalTransfer { get; set; }
    // 0: each thread issues its next request as soon as the previous one completes (closed loop).
    public double RateMiBps { get; set; }
    // Instead of totalTransfer, with a rateMiBps: "30m", "1h30m", "90s" or "01:30:00". The amount is rateMiBps × duration.
    public string? Duration { get; set; }
    public List<BenchFileSet> FileSets { get; set; } = [];
    public List<BenchWorkload> Workloads { get; set; } = [];
}
public sealed class BenchFileSet
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public string Size { get; set; } = "1MiB";
    public double SizeJitter { get; set; }
}
public sealed class BenchWorkload
{
    public string Name { get; set; } = "";
    public double Weight { get; set; } = 1;
    public string FileSet { get; set; } = "";
    public BenchOperation Operation { get; set; }
    public BenchPattern Pattern { get; set; }
    public string BlockSize { get; set; } = "1MiB";
    public string RunLength { get; set; } = "64MiB";
    public BenchFileOrder FileOrder { get; set; }
}
public sealed class BenchmarkPlan
{
    public BenchmarkConfig Config { get; set; } = new();
    public ReplayMode RecommendedTiming { get; set; }
    public int Files { get; set; }
    public long InitialBytes { get; set; }
    public long Operations { get; set; }
    public long PlannedReadBytes { get; set; }
    public long PlannedWriteBytes { get; set; }
    // Bytes written into files the run creates (wholeFile writes such as render outputs): disk space the run needs.
    public long NewFileBytes { get; set; }
    // With a rateMiBps: the planned run time. Null at full speed, where the test target decides it.
    public double? PlannedSeconds { get; set; }
    public Dictionary<string,long> WorkloadBytes { get; set; } = [];
}

// SplitMix64: the same sequence on every .NET version and machine, unlike System.Random.
public struct SeededRandom(ulong state)
{
    public static SeededRandom For(int seed,string stream) => new(BitConverter.ToUInt64(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(seed+":"+stream))));
    public ulong Next()
    {
        ulong z=state+=0x9E3779B97F4A7C15UL;
        z=(z^(z>>30))*0xBF58476D1CE4E5B9UL; z=(z^(z>>27))*0x94D049BB133111EBUL;
        return z^(z>>31);
    }
    public double NextDouble() => (Next()>>11)*(1.0/(1UL<<53));
    public long NextLong(long exclusiveMax) => exclusiveMax<=1?0:(long)(Next()%(ulong)exclusiveMax);
}

// Turns a benchmark configuration into a synthetic session in the recorder's own format, so the existing dataset
// generator and replay engine run it. The same configuration and seed always produce byte-identical events.jsonl.
public static class BenchmarkPlanner
{
    public const string PlanFile="benchmark.json";
    public const string Root=@"B:\bench";
    private const long MaxEvents=20_000_000;
    // Closed-loop plans only need an issue order that interleaves threads fairly; this nominal speed sets that order.
    private const double NominalBytesPerUs=100*1048576/1_000_000.0, OpOverheadUs=20;
    private static readonly JsonSerializerOptions ConfigOptions=new(JsonStore.Options) { ReadCommentHandling=JsonCommentHandling.Skip,AllowTrailingCommas=true,PropertyNameCaseInsensitive=true };

    public static BenchmarkConfig Load(string path) =>
        JsonSerializer.Deserialize<BenchmarkConfig>(File.ReadAllText(path),ConfigOptions) ?? throw new InvalidDataException("構成ファイルが空です。");

    public static long ParseSize(string text)
    {
        var m=Regex.Match(text??"",@"^\s*([0-9]+(?:\.[0-9]+)?)\s*([KMGT]i?B|B)?\s*$",RegexOptions.IgnoreCase);
        if(!m.Success) throw new InvalidDataException($"サイズの書式が不正です: {text}（例: 512KiB, 8MiB, 1GiB, 1GB）");
        double value=double.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture);
        string unit=m.Groups[2].Value.ToUpperInvariant();
        double scale=unit switch { ""or"B"=>1,"KIB"=>1L<<10,"MIB"=>1L<<20,"GIB"=>1L<<30,"TIB"=>1L<<40,"KB"=>1e3,"MB"=>1e6,"GB"=>1e9,"TB"=>1e12,_=>throw new InvalidDataException(text) };
        double bytes=Math.Round(value*scale);
        if(bytes<1 || bytes>long.MaxValue/2) throw new InvalidDataException($"サイズが範囲外です: {text}");
        return (long)bytes;
    }

    // The planned amount: totalTransfer, or rateMiBps × duration. A duration needs a fixed rate: at full speed the test
    // target decides how long the same amount takes.
    public static long TotalBytes(BenchmarkConfig config)
    {
        if(config.RateMiBps<0 || double.IsNaN(config.RateMiBps)) throw new InvalidDataException("rateMiBps は0以上で指定してください。");
        if(string.IsNullOrWhiteSpace(config.Duration)) return ParseSize(string.IsNullOrWhiteSpace(config.TotalTransfer)?"4GiB":config.TotalTransfer);
        if(!string.IsNullOrWhiteSpace(config.TotalTransfer)) throw new InvalidDataException("totalTransfer と duration は、どちらか一方だけを指定してください。");
        if(!(config.RateMiBps>0)) throw new InvalidDataException("duration は rateMiBps（一定速度）と一緒に指定してください。全速（rateMiBps: 0）では、試験先の速さで時間が変わるため、時間を決められません。");
        return (long)Math.Round(config.RateMiBps*1048576*ParseDuration(config.Duration).TotalSeconds);
    }
    public static TimeSpan ParseDuration(string text)
    {
        var m=Regex.Match(text??"",@"^\s*(?:([0-9]+(?:\.[0-9]+)?)\s*h)?\s*(?:([0-9]+(?:\.[0-9]+)?)\s*m(?:in)?)?\s*(?:([0-9]+(?:\.[0-9]+)?)\s*s)?\s*$",RegexOptions.IgnoreCase);
        double Part(int group)=>m.Groups[group].Success?double.Parse(m.Groups[group].Value,CultureInfo.InvariantCulture):0;
        TimeSpan value;
        if(m.Success && (m.Groups[1].Success || m.Groups[2].Success || m.Groups[3].Success)) value=TimeSpan.FromSeconds(Part(1)*3600+Part(2)*60+Part(3));
        else if(TimeSpan.TryParseExact(text?.Trim(),[@"h\:mm\:ss",@"hh\:mm\:ss"],CultureInfo.InvariantCulture,out var clock)) value=clock;
        else throw new InvalidDataException($"duration の書式が不正です: {text}（例: 30m, 1h30m, 90s, 01:30:00）");
        if(value<TimeSpan.FromSeconds(1) || value>TimeSpan.FromDays(7)) throw new InvalidDataException($"duration は1秒〜7日で指定してください: {text}");
        return value;
    }
    // One line for summaries: "合計 20GiB · 全速", "合計 20GiB · 50 MiB/s（約 6分50秒）" or "30分 × 50 MiB/s（合計 87.9 GiB）".
    public static string Describe(BenchmarkConfig c)
    {
        long total=TotalBytes(c);
        if(!string.IsNullOrWhiteSpace(c.Duration)) return $"{Clock(ParseDuration(c.Duration).TotalSeconds)} × {c.RateMiBps:N0} MiB/s（合計 {Size(total)}）";
        return $"合計 {Size(total)} · "+(c.RateMiBps>0?$"{c.RateMiBps:N0} MiB/s（約 {Clock(total/(c.RateMiBps*1048576))}）":"全速");
    }
    // Free space the run needs on the test target: the dummy (when it is still to be generated) and the new files the run
    // writes. Drive letters only; for a UNC share the storage reports its own errors.
    public static void CheckSpace(BenchmarkPlan plan,string target,bool includeDummy)
    {
        var root=Path.GetPathRoot(Path.GetFullPath(target));
        if(root is not { Length:3 }) return;
        long free=new DriveInfo(root).AvailableFreeSpace,need=plan.NewFileBytes+(includeDummy?plan.InitialBytes:0);
        if(need>free) throw new IOException($"試験先の空き容量が足りません: 必要 {Size(need)}（{(includeDummy?$"ダミー {Size(plan.InitialBytes)} + ":"")}実行中に新しく書くファイル {Size(plan.NewFileBytes)}）／ 空き {Size(free)}。totalTransfer・duration・fileSets を小さくするか、別の試験先を選んでください。");
    }
    public static string Size(long bytes)=>bytes>=1L<<30?$"{bytes/1073741824.0:N1} GiB":$"{bytes/1048576.0:N1} MiB";
    public static string Clock(double seconds)
    {
        var t=TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours>=1?$"{(int)t.TotalHours}時間{(t.Minutes>0?$"{t.Minutes}分":"")}":t.TotalMinutes>=1?$"{t.Minutes}分{(t.Seconds>0?$"{t.Seconds}秒":"")}":$"{t.Seconds}秒";
    }

    public static BenchmarkPlan Create(BenchmarkConfig config,string output,IProgress<WorkProgress>? progress=null)
    {
        output=Path.GetFullPath(output);
        if(Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("ベンチマークの保存先は新しいフォルダまたは空のフォルダを指定してください。");
        if(config.Threads is < 1 or > 256) throw new InvalidDataException("threads は1〜256で指定してください。");
        long total=TotalBytes(config);
        var sets=new Dictionary<string,(BenchFileSet Set,long Size,List<(string Path,long Size)> Files)>(StringComparer.OrdinalIgnoreCase);
        config.Seed=Seeds.Normalize(config.Seed) ?? throw new InvalidDataException("seed には文字か数字を含めてください。");
        int seed=Seeds.Value(config.Seed);
        var random=SeededRandom.For(seed,"files");
        foreach(var set in config.FileSets)
        {
            if(string.IsNullOrWhiteSpace(set.Name) || set.Name.IndexOfAny(Path.GetInvalidFileNameChars())>=0) throw new InvalidDataException($"fileSets の name が不正です: {set.Name}");
            if(set.Count<0 || set.SizeJitter is < 0 or >= 1) throw new InvalidDataException($"fileSets[{set.Name}] の count / sizeJitter が範囲外です。");
            long size=ParseSize(set.Size); var files=new List<(string,long)>(set.Count);
            for(int i=0;i<set.Count;i++)
                files.Add(($@"{Root}\{set.Name}\{set.Name}_{i+1:000000}.bin",Math.Max(1,(long)Math.Round(size*(1+set.SizeJitter*(2*random.NextDouble()-1))))));
            if(!sets.TryAdd(set.Name,(set,size,files))) throw new InvalidDataException($"fileSets の name が重複しています: {set.Name}");
        }
        if(config.Workloads.Count==0) throw new InvalidDataException("workloads を1つ以上指定してください。");
        var workloads=config.Workloads.Select(w=>
        {
            if(!(w.Weight>0)) throw new InvalidDataException($"workloads[{w.Name}] の weight は正の値にしてください。");
            if(!sets.TryGetValue(w.FileSet,out var set)) throw new InvalidDataException($"workloads[{w.Name}] の fileSet が見つかりません: {w.FileSet}");
            long block=ParseSize(w.BlockSize),run=ParseSize(w.RunLength);
            if(block is < 512 or > 64L*1024*1024) throw new InvalidDataException($"workloads[{w.Name}] の blockSize は512B〜64MiBで指定してください（再生の上限）。");
            if(run<block) throw new InvalidDataException($"workloads[{w.Name}] の runLength は blockSize 以上にしてください。");
            if(set.Files.Count==0 && !(w.Operation==BenchOperation.Write && w.Pattern==BenchPattern.WholeFile))
                throw new InvalidDataException($"workloads[{w.Name}] は既存ファイルを使いますが、fileSet {w.FileSet} の count が0です。");
            return (Config:w,Set:set,Block:block,Run:run);
        }).ToArray();
        double weightSum=workloads.Sum(w=>w.Config.Weight);

        Directory.CreateDirectory(output);
        var plan=new BenchmarkPlan { Config=config,RecommendedTiming=config.RateMiBps>0?ReplayMode.Timeline:ReplayMode.ThinkTime,PlannedSeconds=config.RateMiBps>0?total/(config.RateMiBps*1048576):null };
        var events=new List<(IoEvent Event,int Thread,int Local)>();
        var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cursors=new long[workloads.Length]; long created=0,planned=0;
        var threads=Enumerable.Range(0,config.Threads).Select(t=>new ThreadState { Random=SeededRandom.For(seed,"thread:"+t) }).ToArray();
        double perThreadBytesPerUs=config.RateMiBps*1048576/1_000_000.0/config.Threads;
        var achieved=new long[workloads.Length]; long nextReport=0;
        // Bursts are generated round-robin over threads until the planned transfer is reached. Weight is a share of the
        // transferred bytes: each burst goes to the workload furthest below its share, so bursts of different sizes still mix in proportion.
        for(int t=0;planned<total;t=(t+1)%config.Threads)
        {
            var thread=threads[t];
            int index=0; double deficit=double.MinValue;
            for(int i=0;i<workloads.Length;i++)
            {
                double d=workloads[i].Config.Weight/weightSum*(planned+1)-achieved[i];
                if(d>deficit) { deficit=d; index=i; }
            }
            var (w,set,block,run)=workloads[index];
            long before=planned;
            string handle=$"T{t}-{++thread.Handles}";
            void Add(IoOperation op,string path,long offset=0,long size=0,int disposition=1)
            {
                var e=new IoEvent { ProcessId=1,ThreadId=t+1,ProcessName="benchmark",Path=path,FileObject=handle,Operation=op,Offset=offset,Size=size,NtStatus=0,MatchesRoot=true,Note="bench:"+w.Name };
                if(op is IoOperation.Read or IoOperation.Write) { e.Transferred=size; e.IoFlags=0; }
                if(op==IoOperation.Open) { e.CreateDisposition=disposition; e.ExtraInfo=disposition==2?2UL:1UL; e.ShareAccess=7; }
                e.StartUs=(long)thread.Time;
                if(config.RateMiBps>0) { thread.Bytes+=size; thread.Time=thread.Bytes/perThreadBytesPerUs; }
                else thread.Time+=size/NominalBytesPerUs+OpOverheadUs;
                events.Add((e,t,thread.Local++));
                if(op==IoOperation.Read) plan.PlannedReadBytes+=size; else if(op==IoOperation.Write) plan.PlannedWriteBytes+=size;
                if(op is IoOperation.Read or IoOperation.Write) { planned+=size; plan.WorkloadBytes[w.Name]=plan.WorkloadBytes.GetValueOrDefault(w.Name)+size; }
            }
            string path; long fileSize;
            if(w.Operation==BenchOperation.Write && w.Pattern==BenchPattern.WholeFile)
            {
                path=$@"{Root}\{set.Set.Name}\new_{++created:000000}.bin";
                fileSize=Math.Max(1,(long)Math.Round(set.Size*(1+set.Set.SizeJitter*(2*thread.Random.NextDouble()-1))));
                Add(IoOperation.Open,path,disposition:2);
                for(long offset=0;offset<fileSize;offset+=block) Add(IoOperation.Write,path,offset,Math.Min(block,fileSize-offset));
                Add(IoOperation.Flush,path); plan.NewFileBytes+=fileSize;
            }
            else
            {
                var file=w.FileOrder==BenchFileOrder.Sequential?set.Files[(int)(cursors[index]++%set.Files.Count)]:set.Files[(int)thread.Random.NextLong(set.Files.Count)];
                (path,fileSize)=file; used.Add(path);
                var op=w.Operation==BenchOperation.Read?IoOperation.Read:IoOperation.Write;
                Add(IoOperation.Open,path);
                if(w.Pattern==BenchPattern.WholeFile)
                    for(long offset=0;offset<fileSize;offset+=block) Add(op,path,offset,Math.Min(block,fileSize-offset));
                else if(w.Pattern==BenchPattern.Sequential)
                {
                    long length=Math.Min(run,fileSize),start=AlignedOffset(ref thread.Random,fileSize-length,block);
                    for(long offset=start;offset<start+length;offset+=block) Add(op,path,offset,Math.Min(block,start+length-offset));
                }
                else
                {
                    long size=Math.Min(block,fileSize);
                    for(long n=Math.Max(1,run/block);n>0;n--) Add(op,path,AlignedOffset(ref thread.Random,fileSize-size,block),size);
                }
                if(op==IoOperation.Write) Add(IoOperation.Flush,path);
            }
            Add(IoOperation.Cleanup,path); Add(IoOperation.Close,path);
            achieved[index]+=planned-before;
            if(planned>=nextReport) { progress?.Report(new("I/O列を作成中",planned,total,$"{events.Count:N0} 操作 · {planned/1048576.0:N0} / {total/1048576.0:N0} MiB")); nextReport=planned+total/200; }
            if(events.Count>MaxEvents) throw new InvalidDataException("I/O列の操作数が上限（2,000万件）を超えます。totalTransfer を減らすか blockSize を大きくしてください。");
        }
        progress?.Report(new("I/O列を保存中",0,0,$"{events.Count:N0} 操作を events.jsonl に保存しています"));
        // Sequence follows the replay order (issue time, then thread), so the window of in-flight requests covers all threads.
        events.Sort((a,b)=>a.Event.StartUs!=b.Event.StartUs?a.Event.StartUs.CompareTo(b.Event.StartUs):a.Thread!=b.Thread?a.Thread.CompareTo(b.Thread):a.Local.CompareTo(b.Local));
        long sequence=0; var next=new Dictionary<int,IoEvent>();
        for(int i=events.Count-1;i>=0;i--)
        {
            var (e,t,_)=events[i];
            // ThinkTime waits StartUs(next) - EndUs(this); making them equal leaves only the real completion as the gap.
            e.EndUs=next.TryGetValue(t,out var following)?following.StartUs:e.StartUs; next[t]=e;
        }
        foreach(var (e,_,_) in events) e.Sequence=++sequence;
        using(var writer=new StreamWriter(Path.Combine(output,"events.jsonl"),false,new UTF8Encoding(false)))
            foreach(var (e,_,_) in events) writer.WriteLine(JsonSerializer.Serialize(e,JsonStore.Options));
        var sizes=sets.Values.SelectMany(s=>s.Files).Where(f=>used.Contains(f.Path)).ToArray();
        using(var writer=new StreamWriter(Path.Combine(output,"sizes.jsonl"),false,new UTF8Encoding(false)))
            foreach(var (path,size) in sizes) writer.WriteLine(JsonSerializer.Serialize(new SizeObservation { Path=path,ObservedUs=0,Size=size },JsonStore.Options));
        File.WriteAllText(Path.Combine(output,"memory.jsonl"),"");
        plan.Files=sizes.Length; plan.InitialBytes=sizes.Sum(f=>f.Size); plan.Operations=events.Count;
        var createdUtc=DateTimeOffset.UtcNow;
        JsonStore.Write(Path.Combine(output,"session.json"),new SessionManifest
        {
            Label=config.Label,State="SyntheticBenchmark",StartedUtc=createdUtc,EndedUtc=createdUtc,EventsWritten=events.Count,EtwLossCountAvailable=true,
            Options=new() { Mode=CaptureMode.Folder,Roots=[Root],ProcessNames=[] },
            Warnings=[$"構成ファイルとseed {config.Seed} から作った合成ベンチマークのI/O列です。実測の記録ではありません。",
                      config.RateMiBps>0?$"目標 {config.RateMiBps:N0} MiB/s で一定に投入します（Timelineで実行）。":"各スレッドが前の要求の完了直後に次を発行します（ThinkTimeで実行）。I/O列上の時刻は投入順を決めるための仮の値です。"]
        });
        JsonStore.Write(Path.Combine(output,PlanFile),plan);
        progress?.Report(new("I/O列を集計中",0,0,"I/O列の内容をHTMLにまとめています"));
        SessionAnalyzer.Export(output);
        progress?.Report(new("I/O列を作成しました",total,total,$"{plan.Operations:N0} 操作 · ダミー {plan.Files:N0} ファイル / {plan.InitialBytes/1073741824.0:N2} GiB"));
        return plan;
    }
    private static long AlignedOffset(ref SeededRandom random,long maxOffset,long block) => maxOffset<=0?0:random.NextLong(maxOffset/block+1)*block;
    private sealed class ThreadState { public SeededRandom Random; public double Time; public long Bytes; public int Local,Handles; }
}

public sealed class BenchmarkWorkloadResult
{
    public string Name { get; set; } = "";
    public long Executed { get; set; }
    public long Failed { get; set; }
    public long Skipped { get; set; }
    public long Reads { get; set; }
    public long Writes { get; set; }
    public long ReadBytes { get; set; }
    public long WriteBytes { get; set; }
    public double MiBps { get; set; }
    public double Iops { get; set; }
    public double? P50Ms { get; set; }
    public double? P95Ms { get; set; }
    public double? P99Ms { get; set; }
}

public static class BenchmarkReport
{
    public static string? ReportPath(string session,ReplaySummary summary)=>
        summary.OutcomeFile is null?null:Path.Combine(session,Path.GetFileNameWithoutExtension(summary.OutcomeFile)+"-report.html");

    // Splits a replay of a benchmark plan by workload and per second, and writes JSON, CSV and a self-contained HTML report.
    // Rates are over the whole run, as workloads overlap.
    public static List<BenchmarkWorkloadResult> Write(string session,ReplaySummary summary,string? target=null)
    {
        if(summary.OutcomeFile is null) return [];
        var workload=new Dictionary<long,string>();
        foreach(var e in RecordLog.Read<IoEvent>(session, RecordLog.Events))
            workload[e.Sequence]=e.Note is { } note && note.StartsWith("bench:",StringComparison.Ordinal)?note[6..]:"(other)";
        var results=new Dictionary<string,(BenchmarkWorkloadResult Result,LatencyHistogram Latency)>();
        var seconds=new SortedDictionary<long,(long Reads,long Writes,long ReadBytes,long WriteBytes)>();
        string outcomes=Path.Combine(session,summary.OutcomeFile);
        foreach(var o in JsonStore.Lines<ReplayOutcome>(outcomes))
        {
            var name=workload.GetValueOrDefault(o.Sequence,"(other)");
            if(!results.TryGetValue(name,out var r)) results[name]=r=(new() { Name=name },new());
            if(o.Status=="Executed") r.Result.Executed++; else if(o.Status=="Failed") r.Result.Failed++; else r.Result.Skipped++;
            if(o.Status!="Executed" || o.Operation is not (IoOperation.Read or IoOperation.Write)) continue;
            r.Latency.Add(o.DurationMs);
            long second=(o.ActualUs+(long)(o.DurationMs*1000))/1_000_000; var s=seconds.GetValueOrDefault(second);
            if(o.Operation==IoOperation.Read) { r.Result.Reads++; r.Result.ReadBytes+=o.Transferred; s.Reads++; s.ReadBytes+=o.Transferred; }
            else { r.Result.Writes++; r.Result.WriteBytes+=o.Transferred; s.Writes++; s.WriteBytes+=o.Transferred; }
            seconds[second]=s;
        }
        double elapsed=Math.Max(summary.ElapsedSeconds,1e-6);
        var list=results.Values.Select(r=>
        {
            r.Result.MiBps=(r.Result.ReadBytes+r.Result.WriteBytes)/1048576.0/elapsed; r.Result.Iops=(r.Result.Reads+r.Result.Writes)/elapsed;
            r.Result.P50Ms=r.Latency.Percentile(.5); r.Result.P95Ms=r.Latency.Percentile(.95); r.Result.P99Ms=r.Latency.Percentile(.99);
            return r.Result;
        }).OrderByDescending(r=>r.ReadBytes+r.WriteBytes).ToList();
        string stem=Path.Combine(session,Path.GetFileNameWithoutExtension(summary.OutcomeFile));
        JsonStore.Write(stem+"-bench.json",new { summary.ElapsedSeconds,totalMiBps=list.Sum(r=>r.MiBps),totalIops=list.Sum(r=>r.Iops),workloads=list });
        using var csv=new StreamWriter(stem+"-timeline.csv",false,new UTF8Encoding(true));
        csv.WriteLine("second,read_ops,write_ops,read_bytes,write_bytes");
        foreach(var (second,s) in seconds) csv.WriteLine($"{second},{s.Reads},{s.Writes},{s.ReadBytes},{s.WriteBytes}");
        WriteHtml(ReportPath(session,summary)!,session,summary,list,seconds,target);
        return list;
    }

    private static void WriteHtml(string path,string session,ReplaySummary summary,List<BenchmarkWorkloadResult> rows,SortedDictionary<long,(long Reads,long Writes,long ReadBytes,long WriteBytes)> seconds,string? target)
    {
        var planFile=Path.Combine(session,BenchmarkPlanner.PlanFile);
        var plan=File.Exists(planFile)?JsonStore.Read<BenchmarkPlan>(planFile):null; var c=plan?.Config;
        string E(string? v)=>System.Net.WebUtility.HtmlEncode(v??"");
        string N(double v,string f="N1")=>v.ToString(f,CultureInfo.InvariantCulture);
        string Ms(double? v)=>v.HasValue?N(v.Value,"N2"):"—";
        string Bytes(long b)=>b>=1L<<30?N(b/1073741824.0,"N2")+" GiB":N(b/1048576.0)+" MiB";
        var conditions=new List<(string Key,string Value)>
        {
            ("実行日時",summary.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),("PC",Environment.MachineName),("試験先",target??"—"),
            ("経過時間",N(summary.ElapsedSeconds)+" 秒"),("投入方式",summary.Options.Mode==ReplayMode.ThinkTime?"全速（完了後すぐ次を発行）":"一定速度（Timeline）")
        };
        if(c is not null)
        {
            conditions.Add(("構成",$"{c.Label} / seed {c.Seed} / {c.Threads} スレッド / {BenchmarkPlanner.Describe(c)}"));
            conditions.Add(("ダミー",$"{plan!.Files:N0} ファイル / {Bytes(plan.InitialBytes)}"));
        }
        double total=rows.Sum(r=>r.MiBps),iops=rows.Sum(r=>r.Iops);
        string Pattern(BenchPattern p)=>p switch { BenchPattern.Sequential=>"シーケンシャル",BenchPattern.Random=>"ランダム",_=>"ファイル全体" };
        double weightSum=c?.Workloads.Sum(w=>Math.Max(0,w.Weight))??0;
        var definitions=c is null?"":string.Join("",c.Workloads.Select(w=>$"<tr><td>{E(w.Name)}</td><td>{(weightSum>0?N(w.Weight/weightSum*100,"N0"):"—")}%</td><td>{w.Operation} · {Pattern(w.Pattern)}</td><td>{E(w.BlockSize)}</td><td>{E(w.Pattern==BenchPattern.WholeFile?"—":w.RunLength)}</td><td>{E(w.FileSet)}</td></tr>"));
        var result=string.Join("",rows.Select(r=>$"<tr><td>{E(r.Name)}</td><td>{N(r.MiBps)}</td><td>{N(r.Iops,"N0")}</td><td>{Ms(r.P50Ms)}</td><td>{Ms(r.P95Ms)}</td><td>{Ms(r.P99Ms)}</td><td>{Bytes(r.ReadBytes)}</td><td>{Bytes(r.WriteBytes)}</td><td>{r.Failed:N0}</td></tr>"));
        var series=JsonSerializer.Serialize(seconds.Select(p=>new { s=p.Key,r=p.Value.ReadBytes/1048576.0,w=p.Value.WriteBytes/1048576.0,ops=p.Value.Reads+p.Value.Writes }));
        var notes=string.Join("",summary.Notes.Select(n=>$"<li>{E(n)}</li>"));
        string html=$$$"""
<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>ベンチマーク結果 — {{{E(c?.Label??"")}}}</title>
<style>
:root{color-scheme:dark;font-family:Segoe UI,Meiryo,sans-serif;background:#0b101b;color:#e7edf7}*{box-sizing:border-box}body{max-width:1240px;margin:auto;padding:36px 28px}h1{font-size:26px;margin:0 0 6px}h2{font-size:17px;margin:0 0 12px}p,.muted{color:#9fadc5;line-height:1.7}.cards{display:grid;grid-template-columns:repeat(4,1fr);gap:14px;margin:22px 0}.card,section{background:#131c2c;border:1px solid #26334a;border-radius:14px;padding:18px}.card b{display:block;font-size:26px;margin-top:6px}section{margin-bottom:18px}table{width:100%;border-collapse:collapse;font-size:13px}td,th{text-align:right;border-bottom:1px solid #27344c;padding:10px 8px}td:first-child,th:first-child{text-align:left}th{color:#9fadc5;font-weight:normal}.kv td{text-align:left}.kv td:first-child{color:#9fadc5;width:140px}canvas{width:100%;height:260px;display:block}#tip{height:22px;font-size:13px;color:#9fadc5}@media(max-width:750px){.cards{grid-template-columns:1fr 1fr}body{padding:18px 12px}}
</style>
<h1>ベンチマーク結果</h1><p>{{{E(c?.Label??"")}}}</p>
<div class="cards"><div class="card">合計<b>{{{N(total)}}} <small>MiB/s</small></b></div><div class="card">IOPS<b>{{{N(iops,"N0")}}}</b></div><div class="card">経過時間<b>{{{N(summary.ElapsedSeconds)}}} <small>秒</small></b></div><div class="card">失敗<b>{{{summary.Failed:N0}}}</b></div></div>
<section><h2>試験条件</h2><table class="kv">{{{string.Join("",conditions.Select(p=>$"<tr><td>{E(p.Key)}</td><td>{E(p.Value)}</td></tr>"))}}}</table></section>
<section><h2>負荷ごとの結果</h2><table><tr><th>負荷</th><th>MiB/s</th><th>IOPS</th><th>P50 ms</th><th>P95 ms</th><th>P99 ms</th><th>Read</th><th>Write</th><th>失敗</th></tr>{{{result}}}</table><p>MiB/sとIOPSは、全体の経過時間で割った値です（負荷は同時に進むため）。レイテンシは1回の読み書きの所要時間です。</p></section>
<section><h2>1秒ごとの転送量</h2><p>青: Read ／ 緑: Write（MiB/s）。グラフにカーソルを重ねると値を表示します。</p><canvas id="timeline"></canvas><div id="tip"></div></section>
{{{(definitions.Length>0?$"<section><h2>負荷の定義</h2><table><tr><th>負荷</th><th>比率</th><th>種類</th><th>ブロック</th><th>まとまり</th><th>ファイル群</th></tr>{definitions}</table></section>":"")}}}
<section><h2>注意</h2><ul class="muted">{{{notes}}}<li>結果は試験先のストレージと、キャッシュの状態に左右されます。ダミーの生成直後に実行した場合は、データがキャッシュに載った状態の値です。</li><li>ダミーは圧縮されにくい乱数です。</li></ul></section>
<script>
const series={{{series}}};
function draw(){const c=document.getElementById('timeline'),d=devicePixelRatio||1;c.width=c.clientWidth*d;c.height=c.clientHeight*d;const x=c.getContext('2d');x.scale(d,d);const w=c.clientWidth,h=c.clientHeight;
if(!series.length){x.fillStyle='#9fadc5';x.fillText('データなし',20,30);return}
const end=Math.max(1,...series.map(p=>p.s)),max=Math.max(1,...series.map(p=>Math.max(p.r,p.w)))*1.1;x.strokeStyle='#28364f';x.fillStyle='#9fadc5';x.font='12px Segoe UI';
for(let i=0;i<5;i++){let y=20+i*(h-50)/4;x.beginPath();x.moveTo(55,y);x.lineTo(w-12,y);x.stroke();x.fillText((max*(1-i/4)).toFixed(1),3,y+4)}
for(const[k,color]of[['r','#74a8ff'],['w','#65ddc8']]){x.beginPath();x.strokeStyle=color;x.fillStyle=color;x.lineWidth=2;series.forEach((p,i)=>{let px=55+p.s/end*(w-70),py=h-30-p[k]/max*(h-50);i?x.lineTo(px,py):x.moveTo(px,py)});x.stroke();
if(series.length<30)series.forEach(p=>{x.beginPath();x.arc(55+p.s/end*(w-70),h-30-p[k]/max*(h-50),3.5,0,7);x.fill()})}
x.fillStyle='#9fadc5';
x.fillText('0 s',55,h-6);x.fillText(end+' s',w-60,h-6);
c.onpointermove=e=>{let t=Math.round((e.offsetX-55)/(w-70)*end),p=series.reduce((a,b)=>Math.abs(b.s-t)<Math.abs(a.s-t)?b:a);document.getElementById('tip').textContent=p.s+' s · Read '+p.r.toFixed(2)+' MiB/s · Write '+p.w.toFixed(2)+' MiB/s · '+p.ops+' IOPS'}}
window.onresize=draw;draw();
</script></html>
""";
        File.WriteAllText(path,html,new UTF8Encoding(false));
    }
}
