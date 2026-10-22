namespace IoRecorder.Core;

// Ties a recording of a replay (in <original record>\replays\<time>) to the original record and the dummy it ran on.
public sealed class ReplayLink
{
    public string OriginalSession { get; set; } = "";
    public string Dataset { get; set; } = "";
    public string? OutcomeFile { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public double ElapsedSeconds { get; set; }
    public ReplayOptions Options { get; set; } = new();
    // The process that ran the replay: its I/O in the recording is the replayed requests; the rest is the test target's own
    // (the OS, antivirus, the search indexer). Missing in links written before it was recorded.
    public int? ProcessId { get; set; }
    // The IO Recorder that ran and recorded the replay.
    public string? Tool { get; set; }
    // Dummy path (as the recording of the replay sees it) → the original recorded path.
    public Dictionary<string,string> Paths { get; set; } = [];
}

// A replay with its I/O on the test target recorded by ETW at the same time (administrator only). The recording covers every
// process's I/O to the dummy folder: the replay itself plus what the OS and other programs did to it (cache fills, lazy writes,
// antivirus scans). Writes of the replay results to the original record are outside it.
public static class ReplayCapture
{
    public const string Folder = "replays";
    public const string LinkFile = "replay-link.json";

    public static async Task<(ReplaySummary Summary, string Trace)> RunAsync(string session, string dataset, ReplayOptions options,
        IProgress<string>? progress = null, CancellationToken token = default, IProgress<WorkProgress>? work = null)
    {
        session = Path.GetFullPath(session); dataset = Path.GetFullPath(dataset);
        var original = JsonStore.Read<SessionManifest>(Path.Combine(session, "session.json"));
        var m = JsonStore.Read<DatasetManifest>(Path.Combine(dataset, DatasetBuilder.Marker));
        string trace = AppPaths.NewFolder(Path.Combine(session, Folder), "replay");
        var link = new ReplayLink { OriginalSession = session, Dataset = dataset, Options = options, ProcessId = Environment.ProcessId, Tool = ToolVersion.Text };
        foreach (var (recorded, relative) in m.Paths)
            if (PathPolicy.TryCombineInside(dataset, relative, out var full)) link.Paths[full] = recorded;
        // The replay runs in this process, so this process's I/O is recorded; only the dummy folder is, so the recording's own
        // files and the replay results (in the original record) stay out. No size queries: they would add requests to the
        // storage under test, and the dummy's sizes are known.
        var recordOptions = new RecordingOptions { Mode = CaptureMode.Folder, Roots = [dataset], ProbeSizes = false, OpenFilesAtStart = false, IncludeChildren = false, IncludeOwnProcess = true };
        var recorder = new EtwRecorder();
        using var stopRecording = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recording = recorder.RecordAsync(trace, recordOptions, $"再生の記録: {original.Label}（{options.Mode}）", stopRecording.Token, progress, () => started.TrySetResult());
        // The recording must run before the first request; a failure to start it surfaces here.
        if (await Task.WhenAny(started.Task, recording) == recording) { await recording; throw new IOException("再生の記録を開始できませんでした。"); }
        ReplaySummary summary;
        try { summary = await new ReplayEngine().RunAsync(session, dataset, options, progress, token, work); }
        catch
        {
            stopRecording.Cancel();
            try { await recording; } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            try { Directory.Delete(trace, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        // The last writes reach the storage through the cache shortly after the replay ends; they belong to the run.
        progress?.Report("再生の記録を確定しています…");
        await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
        stopRecording.Cancel();
        await recording;
        link.OutcomeFile = summary.OutcomeFile; link.StartedUtc = summary.StartedUtc; link.ElapsedSeconds = summary.ElapsedSeconds;
        JsonStore.Write(Path.Combine(trace, LinkFile), link);
        summary.TraceFolder = Path.GetRelativePath(session, trace);
        if (summary.OutcomeFile is { } outcome) JsonStore.Write(Path.Combine(session, Path.ChangeExtension(outcome, ".json")), summary);
        return (summary, trace);
    }

    // The recordings of replays of a record, newest first.
    public static List<(string Folder, ReplayLink Link)> List(string session)
    {
        string root = Path.Combine(session, Folder);
        if (!Directory.Exists(root)) return [];
        var traces = new List<(string, ReplayLink)>();
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            string file = Path.Combine(folder, LinkFile);
            if (!File.Exists(file) || !File.Exists(Path.Combine(folder, "session.json"))) continue;
            try { traces.Add((folder, JsonStore.Read<ReplayLink>(file))); } catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException) { }
        }
        return traces.OrderByDescending(t => t.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public enum ComparisonUnit { Seconds, Bytes, MiBps, Iops, Ms, Count }
public sealed record ComparisonValue(string Item, ComparisonUnit Unit, double? Original, double? Replay);
public sealed record OperationComparison(string Operation, long Original, long Replay);
// Who issued the I/O on each side. In the recording of the replay, the replay process sent every replayed request (whichever
// process issued it originally); the other processes there are the test target's own.
public sealed record ProcessComparison(string Process, long OriginalOperations, long ReplayOperations, long OriginalReadBytes, long ReplayReadBytes, long OriginalWriteBytes, long ReplayWriteBytes);
// All: every process on both sides, as recorded. Replayed: the requests the replay was asked to send (the original within the
// replay's scope) against what the replay process sent, without the test target's own I/O and the dummy's marker file.
public enum ComparisonScope { All, Replayed }

public sealed class FileComparison
{
    public string Path { get; set; } = "";
    // 両方 / 元のみ / 再生のみ
    public string Presence { get; set; } = "";
    public long OriginalReadBytes { get; set; }
    public long ReplayReadBytes { get; set; }
    public long OriginalWriteBytes { get; set; }
    public long ReplayWriteBytes { get; set; }
    public double? OriginalP95Ms { get; set; }
    public double? ReplayP95Ms { get; set; }
}

// The original record next to the recording of one of its replays. Both are measured by ETW at the same point (the file
// system), so latency compares directly. Timelines start at each side's first second with application I/O.
public sealed class ReplayComparison
{
    public ReplayLink Link { get; set; } = new();
    public AnalysisResult Original { get; set; } = new();
    public AnalysisResult Replay { get; set; } = new();
    public List<SecondMetrics> OriginalTimeline { get; set; } = [];
    public List<SecondMetrics> ReplayTimeline { get; set; } = [];
    public List<ComparisonValue> Values { get; set; } = [];
    public List<FileComparison> Files { get; set; } = [];
    public List<OperationComparison> Operations { get; set; } = [];
    public List<ProcessComparison> Processes { get; set; } = [];
    public ComparisonScope Scope { get; set; }
    public int? ReplayProcessId { get; set; }
    // The folders of the original record both sides are narrowed to; empty for every location.
    public List<string> Locations { get; set; } = [];
    public const string ReplayProcessSuffix = "（再生）";
    // Windows Search read the test target during the replay (its I/O mixes into the test); null when it did not.
    public string? IndexerNote => Processes.Where(p => p.Process.StartsWith("Search", StringComparison.OrdinalIgnoreCase)).Sum(p => p.ReplayOperations) is > 0 and var n
        ? $"再生中に、Windowsの検索インデクサーが試験先に {n:N0} 件のI/Oを出していました。インデックスの対象外のフォルダ（例: C:\\IOTest）で試すと、比較が安定します。" : null;

    // locations: both sides narrowed to these folders of the original record (the replay side by the original paths its dummy
    // paths stand for).
    // The IO Recorder of each side; a replay made by another version than the record is pointed out.
    public static string Tools(string? recorded, string? replayed) =>
        $"記録したツール v{recorded ?? "不明"} ／ 再生したツール v{replayed ?? "不明"}" + (recorded is not null && replayed is not null && recorded != replayed ? "（バージョンが違います）" : "");

    public static ReplayComparison Build(string session, string trace, ComparisonScope scope = ComparisonScope.All, LocationFilter? locations = null)
    {
        var link = JsonStore.Read<ReplayLink>(Path.Combine(trace, ReplayCapture.LinkFile));
        var paths = new Dictionary<string,string>(link.Paths, StringComparer.OrdinalIgnoreCase);
        // The dummy's marker file (and the temporary file it is saved through) is the tool's own, not part of the test.
        string marker = Path.Combine(link.Dataset, DatasetBuilder.Marker);
        bool Tool(IoEvent e) => e.Path.StartsWith(marker, StringComparison.OrdinalIgnoreCase);
        locations ??= new();
        bool InReplay(IoEvent e) => locations.IsEmpty || paths.TryGetValue(e.Path, out var p) && locations.Includes(p);
        var (processes, replayPid) = CountProcesses(session, trace, link, Tool, e => locations.Includes(e.Path), InReplay);
        AnalysisResult original, replay;
        if (scope == ComparisonScope.All) { original = SessionAnalyzer.Load(session, null, locations); replay = Measure(trace, e => !Tool(e) && InReplay(e)); }
        else
        {
            if (replayPid is not { } pid) throw new InvalidDataException("この再生の記録では、再生したプロセスを特定できません。");
            // What the replay was asked to send: the original within its scope and the folders its dummy was made for.
            var o = link.Options; var made = LocationFilter.Of(o.Locations);
            original = o.ProcessOnly || o.RootOnly || !made.IsEmpty || !locations.IsEmpty
                ? Measure(session, e => !(o.ProcessOnly && !e.MatchesProcess) && !(o.RootOnly && !e.MatchesRoot) && made.Includes(e.Path) && locations.Includes(e.Path)) : SessionAnalyzer.Load(session);
            replay = Measure(trace, e => e.ProcessId == pid && !Tool(e) && InReplay(e));
        }
        var c = new ReplayComparison { Link = link, Original = original, Replay = replay, Processes = processes, Scope = scope, ReplayProcessId = replayPid, Locations = [.. locations.Folders] };
        var (originalTimeline, originalSeconds) = Align(original);
        var (replayTimeline, replaySeconds) = Align(replay);
        c.OriginalTimeline = originalTimeline; c.ReplayTimeline = replayTimeline;
        static double Per(long amount, double seconds) => amount / Math.Max(1, seconds);
        static double Max(List<SecondMetrics> t, Func<SecondMetrics,double> value) => t.Select(value).DefaultIfEmpty(0).Max();
        c.Values =
        [
            new("I/Oがあった時間", ComparisonUnit.Seconds, originalSeconds, replaySeconds),
            new("Read 合計", ComparisonUnit.Bytes, original.ReadBytes, replay.ReadBytes),
            new("Write 合計", ComparisonUnit.Bytes, original.WriteBytes, replay.WriteBytes),
            new("Read 平均", ComparisonUnit.MiBps, Per(original.ReadBytes, originalSeconds) / 1048576, Per(replay.ReadBytes, replaySeconds) / 1048576),
            new("Read 最大（1秒）", ComparisonUnit.MiBps, Max(originalTimeline, s => s.ReadMiB), Max(replayTimeline, s => s.ReadMiB)),
            new("Write 平均", ComparisonUnit.MiBps, Per(original.WriteBytes, originalSeconds) / 1048576, Per(replay.WriteBytes, replaySeconds) / 1048576),
            new("Write 最大（1秒）", ComparisonUnit.MiBps, Max(originalTimeline, s => s.WriteMiB), Max(replayTimeline, s => s.WriteMiB)),
            new("IOPS 平均", ComparisonUnit.Iops, Per(original.Reads + original.Writes, originalSeconds), Per(replay.Reads + replay.Writes, replaySeconds)),
            new("IOPS 最大（1秒）", ComparisonUnit.Iops, Max(originalTimeline, s => s.Reads + s.Writes), Max(replayTimeline, s => s.Reads + s.Writes)),
            new("レイテンシ P50", ComparisonUnit.Ms, original.P50Ms, replay.P50Ms),
            new("レイテンシ P95", ComparisonUnit.Ms, original.P95Ms, replay.P95Ms),
            new("レイテンシ P99", ComparisonUnit.Ms, original.P99Ms, replay.P99Ms),
            new("Read 回数", ComparisonUnit.Count, original.Reads, replay.Reads),
            new("Write 回数", ComparisonUnit.Count, original.Writes, replay.Writes),
            new("ページングI/O Read", ComparisonUnit.Bytes, original.PagingReadBytes, replay.PagingReadBytes),
            new("ページングI/O Write", ComparisonUnit.Bytes, original.PagingWriteBytes, replay.PagingWriteBytes),
            new("失敗した操作", ComparisonUnit.Count, original.FailedOperations, replay.FailedOperations),
            new("高速I/Oの拒否（数えない）", ComparisonUnit.Count, original.FastIoRefusals, replay.FastIoRefusals),
        ];
        // Files by their original path; the dummy's own marker file is the tool's, not the replayed workload's.
        var byPath = new Dictionary<string,FileComparison>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in original.Files.Where(f => f.Reads + f.Writes > 0))
            byPath[f.Path] = new() { Path = f.Path, Presence = "元のみ", OriginalReadBytes = f.ReadBytes, OriginalWriteBytes = f.WriteBytes, OriginalP95Ms = f.P95Ms };
        foreach (var f in replay.Files.Where(f => f.Reads + f.Writes > 0))
        {
            string path = paths.GetValueOrDefault(f.Path) ?? f.Path;
            if (!byPath.TryGetValue(path, out var row)) byPath[path] = row = new() { Path = path, Presence = "再生のみ" };
            else row.Presence = "両方";
            row.ReplayReadBytes += f.ReadBytes; row.ReplayWriteBytes += f.WriteBytes; row.ReplayP95Ms = f.P95Ms;
        }
        c.Files = byPath.Values.OrderByDescending(f => Math.Max(f.OriginalReadBytes + f.OriginalWriteBytes, f.ReplayReadBytes + f.ReplayWriteBytes)).ToList();
        c.Operations = original.Operations.Keys.Union(replay.Operations.Keys)
            .Select(op => new OperationComparison(op, original.Operations.GetValueOrDefault(op), replay.Operations.GetValueOrDefault(op)))
            .OrderByDescending(o => Math.Max(o.Original, o.Replay)).ToList();
        return c;
    }

    // The same measures as the saved analysis, over the events that pass filter.
    private static AnalysisResult Measure(string folder, Func<IoEvent,bool> filter)
    {
        var metrics = new LiveMetrics(); var operations = new Dictionary<string,long>();
        foreach (var e in RecordLog.Read<IoEvent>(folder, RecordLog.Events))
        {
            if (!e.IsFileIo || !filter(e)) continue;
            metrics.Add(e);
            if (!e.IsFastIoRefusal) { var op = SessionAnalyzer.OperationName(e); operations[op] = operations.GetValueOrDefault(op) + 1; }
        }
        var result = metrics.Snapshot(int.MaxValue); result.Operations = operations;
        return result;
    }

    // Operations and application bytes per process on both sides; the replay process is marked. Without its id in the link
    // (older recordings), it is the IO Recorder process with the most I/O in the recording of the replay.
    private static (List<ProcessComparison> Rows, int? ReplayPid) CountProcesses(string session, string trace, ReplayLink link, Func<IoEvent,bool> tool, Func<IoEvent,bool> inOriginal, Func<IoEvent,bool> inReplay)
    {
        Dictionary<(int Pid, string Name), long[]> Count(string folder, Func<IoEvent,bool> include)
        {
            var counts = new Dictionary<(int, string), long[]>();
            foreach (var e in RecordLog.Read<IoEvent>(folder, RecordLog.Events))
            {
                if (!e.IsFileIo || e.IsFastIoRefusal || tool(e) || !include(e)) continue;
                var key = (e.ProcessId, string.IsNullOrEmpty(e.ProcessName) ? $"PID {e.ProcessId}" : e.ProcessName);
                if (!counts.TryGetValue(key, out var c)) counts[key] = c = new long[3];
                c[0]++;
                if (e.Failed || e.IsPagingIo) continue;
                if (e.Operation == IoOperation.Read) c[1] += Math.Max(0, e.Transferred ?? e.Size);
                if (e.Operation == IoOperation.Write) c[2] += Math.Max(0, e.Transferred ?? e.Size);
            }
            return counts;
        }
        var original = Count(session, inOriginal); var replay = Count(trace, inReplay);
        int? pid = link.ProcessId ?? replay.Where(p => p.Key.Name.StartsWith("IORecorder", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Value[0]).Select(p => (int?)p.Key.Pid).FirstOrDefault();
        // original operations, replay operations, original read, replay read, original write, replay write
        var rows = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
        long[] Row(string name) => rows.TryGetValue(name, out var r) ? r : rows[name] = new long[6];
        foreach (var ((_, name), v) in original) { var r = Row(name); r[0] += v[0]; r[2] += v[1]; r[4] += v[2]; }
        foreach (var ((p, name), v) in replay) { var r = Row(p == pid ? name + ReplayProcessSuffix : name); r[1] += v[0]; r[3] += v[1]; r[5] += v[2]; }
        return (rows.Select(r => new ProcessComparison(r.Key, r.Value[0], r.Value[1], r.Value[2], r.Value[3], r.Value[4], r.Value[5]))
            .OrderByDescending(r => Math.Max(r.OriginalOperations, r.ReplayOperations)).ToList(), pid);
    }

    // The seconds from the first to the last second with application Reads or Writes, renumbered from 0 (for the graph), and
    // the exact time from the start of the first to the end of the last of them (whole seconds would be off by up to 2 s).
    private static (List<SecondMetrics> Timeline, double Seconds) Align(AnalysisResult a)
    {
        var busy = a.Timeline.Where(s => s.Reads + s.Writes > 0).ToList();
        if (busy.Count == 0) return ([], 0);
        int first = busy.Min(s => s.Second), last = busy.Max(s => s.Second);
        var aligned = a.Timeline.Where(s => s.Second >= first && s.Second <= last).Select(s => new SecondMetrics
        {
            Second = s.Second - first, Reads = s.Reads, Writes = s.Writes, ReadBytes = s.ReadBytes, WriteBytes = s.WriteBytes,
            ReadP50Ms = s.ReadP50Ms, ReadP95Ms = s.ReadP95Ms, WriteP50Ms = s.WriteP50Ms, WriteP95Ms = s.WriteP95Ms
        }).ToList();
        return (aligned, a.FirstIoUs is { } start && a.LastIoEndUs is { } end ? (end - start) / 1_000_000.0 : last - first + 1);
    }
}
