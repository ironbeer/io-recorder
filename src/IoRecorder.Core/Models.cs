using System.Text.Json;
using System.Text.Json.Serialization;

namespace IoRecorder.Core;

public enum CaptureMode { Process, Folder, Hybrid }
// DirectoryNotify: a request to be told about changes in a folder (stays pending until something changes).
// MapView / UnmapView: a view of a file mapped into or out of a process's memory (no I/O by itself; the reads and writes of
// mapped pages appear as paging I/O and hard faults).
public enum IoOperation { Open, Read, Write, Cleanup, Close, Flush, SetEndOfFile, SetAllocation, Delete, Rename, QueryInfo, DirectoryEnumerate, SetInfo, FsControl, HardFault, DiskRead, DiskWrite, DirectoryNotify, MapView, UnmapView }
public enum ReplayMode { Timeline, ThinkTime }
public enum PagingReplay { UnmatchedReads, None, All }

public sealed class RecordingOptions
{
    public CaptureMode Mode { get; set; } = CaptureMode.Hybrid;
    // Executable names, matched exactly (case-insensitive, with or without ".exe").
    public string[] ProcessNames { get; set; } = [];
    public int[] ProcessIds { get; set; } = [];
    public string[] Roots { get; set; } = [];
    // Resolved by the unelevated caller when possible: an elevated token may not see the user's drive letters.
    public ResolvedRoot[] ResolvedRoots { get; set; } = [];
    public bool IncludeChildren { get; set; } = true;
    public bool ProbeSizes { get; set; } = true;
    // Look up the files already open at the start (OpenFiles), so that their later I/O has a path.
    public bool OpenFilesAtStart { get; set; } = true;
    public bool CollectDisk { get; set; } = true;
    // The recorder's own process is left out (its writes of the recording itself), except when this process does the work
    // being recorded: a replay recorded while it runs (ReplayCapture), with only the dummy folder recorded.
    public bool IncludeOwnProcess { get; set; }
    public int BufferMB { get; set; } = 128;
    public int QueueCapacity { get; set; } = 65_536;
    public string? LucidExecutable { get; set; }
    public string? LucidInstance { get; set; }

    public void Validate(string output)
    {
        ProcessNames = ProcessNames.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        bool processes = ProcessNames.Length > 0 || ProcessIds.Length > 0, roots = Roots.Any(r => !string.IsNullOrWhiteSpace(r));
        if (Mode == CaptureMode.Process && !processes)
            throw new ArgumentException("対象プロセス名またはPIDを指定してください。");
        if (Mode == CaptureMode.Folder && !roots)
            throw new ArgumentException("対象フォルダを指定してください。");
        // Hybrid is "process match OR folder match", so either side alone is enough.
        if (Mode == CaptureMode.Hybrid && !processes && !roots)
            throw new ArgumentException("ハイブリッドでは、対象プロセスか対象フォルダの少なくとも一方を指定してください。");
        Roots = Roots.Select(PathPolicy.NormalizeRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var given = ResolvedRoots.Where(r => !string.IsNullOrWhiteSpace(r.UserPath)).ToArray();
        ResolvedRoots = Roots.Select(root => given.FirstOrDefault(r => PathPolicy.NormalizeRoot(r.UserPath).Equals(root, StringComparison.OrdinalIgnoreCase)) ?? RootResolver.Resolve(root)).ToArray();
        ProcessNames = ProcessNames.Select(p => Path.GetFileNameWithoutExtension(p.Trim())).Where(p => p.Length > 0).ToArray();
        foreach (var root in Roots)
            if (PathPolicy.IsUnder(output, root))
                throw new ArgumentException("記録の保存先は計測対象フォルダの外にしてください。");
        if (QueueCapacity < 1024 || BufferMB < 16 || BufferMB > 1024)
            throw new ArgumentException("バッファ設定が範囲外です。");
    }
}

// Numeric progress of a long step; Total 0 means the amount of work is not known yet.
public sealed record WorkProgress(string Stage,long Done,long Total,string Detail)
{
    public double Fraction => Total>0?Math.Clamp((double)Done/Total,0,1):0;
}

public sealed class ResolvedRoot
{
    public string UserPath { get; set; } = "";
    public string? DevicePath { get; set; }
    public string? UncPath { get; set; }
}

public sealed class SessionManifest
{
    // 2: IoFlags, resolved roots and capture diagnostics. Version 1 sessions remain readable.
    public const int CurrentSchema = 2;
    public int SchemaVersion { get; set; } = CurrentSchema;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedUtc { get; set; }
    public string Machine { get; set; } = Environment.MachineName;
    public string OS { get; set; } = Environment.OSVersion.VersionString;
    public int CpuCount { get; set; } = Environment.ProcessorCount;
    public ulong TotalPhysicalBytes { get; set; }
    public RecordingOptions Options { get; set; } = new();
    public string State { get; set; } = "Recording";
    public long EventsWritten { get; set; }
    public long EtwEventsLost { get; set; }
    public bool EtwLossCountAvailable { get; set; }
    public long QueueEventsDropped { get; set; }
    public long IncompleteOperations { get; set; }
    public long UnresolvedPaths { get; set; }
    public long RootMatchedEvents { get; set; }
    public Dictionary<string,long> UnresolvedDevicePrefixes { get; set; } = [];
    public Dictionary<string,long> UnhandledFileIoOpcodes { get; set; } = [];
    // I/O whose file name was not known (a file opened before the recording whose path could not be found), by process.
    public Dictionary<string,long> UnresolvedPathsByProcess { get; set; } = [];
    // Files already open when the recording started whose names were looked up to name their later I/O (open-files.jsonl).
    public int OpenFilesAtStart { get; set; }
    // The process recording it and when the counters above were last saved: a record still marked Recording whose recorder
    // is gone was cut off (the tool or the PC stopped) and is finished as Interrupted when opened.
    public int? RecorderProcessId { get; set; }
    public DateTimeOffset? RecorderStartedUtc { get; set; }
    public DateTimeOffset? CheckpointUtc { get; set; }
    public List<string> Warnings { get; set; } = [];
}

public sealed class IoEvent
{
    public long Sequence { get; set; }
    public long StartUs { get; set; }
    public long? EndUs { get; set; }
    public IoOperation Operation { get; set; }
    public int ProcessId { get; set; }
    public int ThreadId { get; set; }
    public string ProcessName { get; set; } = "";
    public string Path { get; set; } = "";
    public string? DestinationPath { get; set; }
    public string? SearchPattern { get; set; }
    public string FileObject { get; set; } = "";
    // The file itself (the same for every handle and process using it, and across renames); not in open events.
    public string? FileKey { get; set; }
    public long Offset { get; set; }
    public long Size { get; set; }
    public long? Transferred { get; set; }
    public uint? NtStatus { get; set; }
    public ulong? ExtraInfo { get; set; }
    public int? InfoClass { get; set; }
    public int? CreateDisposition { get; set; }
    public uint? CreateOptions { get; set; }
    public uint? ShareAccess { get; set; }
    public uint? IoFlags { get; set; }
    public bool MatchesProcess { get; set; }
    public bool MatchesRoot { get; set; }
    // Another process's I/O on a file the target process uses (the OS writing its cache out, antivirus, the search indexer):
    // kept for analysis, but not counted as the application's requests and not replayed.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool MatchesFile { get; set; }
    public bool IsDirectory { get; set; }
    // Physical disk I/O: the disk it went to.
    public int? DiskNumber { get; set; }
    public string? Note { get; set; }
    [JsonIgnore] public bool Failed => NtStatus.HasValue && unchecked((int)NtStatus.Value) < 0;
    // STATUS_FLT_DISALLOW_FAST_IO: a filter driver refused the fast I/O path, and the I/O manager sends the same request again
    // as an IRP right away (recorded as its own event). It is one application request, not a failure.
    public const uint FastIoRefusedStatus = 0xC01C0004;
    [JsonIgnore] public bool IsFastIoRefusal => NtStatus == FastIoRefusedStatus;
    // IRP_PAGING_IO (0x2) / IRP_SYNCHRONOUS_PAGING_IO (0x40): cache fills, read-ahead, lazy writes and mapped-page I/O.
    [JsonIgnore] public bool IsPagingIo => IoFlags is { } flags && (flags & 0x42) != 0;
    [JsonIgnore] public bool IsFileIo => Operation is not (IoOperation.DiskRead or IoOperation.DiskWrite or IoOperation.HardFault or IoOperation.MapView or IoOperation.UnmapView);
    [JsonIgnore] public bool ByOtherProcess => MatchesFile && !MatchesProcess && !MatchesRoot;
    [JsonIgnore] public double? DurationMs => EndUs.HasValue ? Math.Max(0, EndUs.Value - StartUs) / 1000.0 : null;
}

public sealed class SizeObservation
{
    public string Path { get; set; } = "";
    public long ObservedUs { get; set; }
    public long? Size { get; set; }
    public bool IsDirectory { get; set; }
    // FILE_ATTRIBUTE_* (read-only, sparse, compressed, offline, a cloud placeholder that recalls its data on access, ...).
    public uint? Attributes { get; set; }
    public DateTimeOffset? CreatedUtc { get; set; }
    public DateTimeOffset? LastWriteUtc { get; set; }
    public string? Error { get; set; }
}

public sealed class FileEntry
{
    public string Path { get; set; } = "";
    public long FirstUs { get; set; }
    public IoOperation FirstOperation { get; set; }
    public bool InitiallyExists { get; set; } = true;
    public bool IsDirectory { get; set; }
    public long? ObservedSize { get; set; }
    public long? ObservedUs { get; set; }
    public long MinimumInitialReadExtent { get; set; }
    public long SuggestedInitialSize { get; set; }
    public string SizeConfidence { get; set; } = "Unknown";
    public bool Modified { get; set; }
}

public sealed class MemorySample
{
    public long TimeUs { get; set; }
    public ulong TotalBytes { get; set; }
    public ulong AvailableBytes { get; set; }
    public ulong CommitBytes { get; set; }
    public ulong CommitLimitBytes { get; set; }
    public ulong SystemCacheBytes { get; set; }
    // File data kept in memory for reuse (standby list) and changed file data not yet written out (modified list).
    public ulong? StandbyBytes { get; set; }
    public ulong? ModifiedBytes { get; set; }
    // CPU use of the whole machine over the last second (100 = every logical processor busy).
    public double? CpuPercent { get; set; }
    public List<ProcessMemory> Processes { get; set; } = [];
    // Bytes received and sent by each network adapter since it started (the change from the previous sample is the traffic).
    public List<NetworkCounter> Network { get; set; } = [];
}
// CpuPercent: the process's CPU use over the last second, 100 = one logical processor.
public sealed record ProcessMemory(int Pid, string Name, long WorkingSetBytes, long PrivateBytes, double? CpuPercent = null);
public sealed record NetworkCounter(string Name, long ReceivedBytes, long SentBytes);

public sealed class SecondMetrics
{
    public int Second { get; set; }
    public long Reads { get; set; }
    public long Writes { get; set; }
    public long ReadBytes { get; set; }
    public long WriteBytes { get; set; }
    public long Metadata { get; set; }
    public long HardFaults { get; set; }
    public long DiskReadBytes { get; set; }
    public long DiskWriteBytes { get; set; }
    public long PagingReadBytes { get; set; }
    public long PagingWriteBytes { get; set; }
    // Latency of the successful application Reads / Writes that started in this second (paging I/O excluded).
    public double? ReadP50Ms { get; set; }
    public double? ReadP95Ms { get; set; }
    public double? WriteP50Ms { get; set; }
    public double? WriteP95Ms { get; set; }
    public double ReadMiB => ReadBytes / 1048576.0;
    public double WriteMiB => WriteBytes / 1048576.0;
}

public sealed class FileMetrics
{
    public string Path { get; set; } = "";
    public long Reads { get; set; }
    public long Writes { get; set; }
    public long ReadBytes { get; set; }
    public long WriteBytes { get; set; }
    public long Metadata { get; set; }
    public long SequentialReads { get; set; }
    public long PagingReadBytes { get; set; }
    public long PagingWriteBytes { get; set; }
    public long MaxReadEnd { get; set; }
    // Latency of this file's successful application Reads and Writes (paging I/O excluded).
    public double? P50Ms { get; set; }
    public double? P95Ms { get; set; }
    public double TotalMiB => (ReadBytes + WriteBytes) / 1048576.0;
    [JsonIgnore] internal long? LastReadEnd;
}

public sealed class AnalysisResult
{
    // A saved analysis.json older than this is recomputed (2: per-second latency in the timeline; 3: access patterns; 4: write
    // patterns; 5: latency per file; 6: fast I/O refusals no longer counted as requests or failures, and a path not found below
    // a file no longer makes that file a folder; 7: the exact span of application I/O; 8: paging reads and writes counted
    // apart from the application's in the operation counts).
    public const int CurrentVersion = 8;
    public int Version { get; set; }
    public long TotalEvents { get; set; }
    public int FileCount { get; set; }
    public double DurationSeconds { get; set; }
    // Start of the first and end of the last application Read or Write (paging I/O excluded), µs from the recording start.
    public long? FirstIoUs { get; set; }
    public long? LastIoEndUs { get; set; }
    public long Reads { get; set; }
    public long Writes { get; set; }
    public long ReadBytes { get; set; }
    public long WriteBytes { get; set; }
    public bool IoFlagsAvailable { get; set; }
    public long PagingReads { get; set; }
    public long PagingWrites { get; set; }
    public long PagingReadBytes { get; set; }
    public long PagingWriteBytes { get; set; }
    public long FailedOperations { get; set; }
    // Requests a filter driver sent back from the fast I/O path; each was sent again as an IRP and is counted there.
    public long FastIoRefusals { get; set; }
    // Other processes' I/O on the target process's files (MatchesFile): outside the application totals, but their paging I/O
    // (the OS reading files into and writing them out of its cache) is in the paging totals.
    public long OtherProcessEvents { get; set; }
    public long MissingCompletions { get; set; }
    public long LatencySamples { get; set; }
    public double? P50Ms { get; set; }
    public double? P95Ms { get; set; }
    public double? P99Ms { get; set; }
    public long UniqueReadBytesLowerBound { get; set; }
    public bool UniqueReadCapped { get; set; }
    public Dictionary<string,long> Operations { get; set; } = [];
    public Dictionary<string,long> SizeDistribution { get; set; } = [];
    public Dictionary<string,long> ExtensionBytes { get; set; } = [];
    public List<SecondMetrics> Timeline { get; set; } = [];
    public List<FileMetrics> Files { get; set; } = [];
    public List<FileEntry> FileManifest { get; set; } = [];
    // How each file was read and written (application requests), and what a chunked store would fetch per split size.
    public List<FileAccessPattern> AccessPatterns { get; set; } = [];
    public List<ChunkEstimate> ChunkEstimates { get; set; } = [];
    public bool AccessRangesCapped { get; set; }
    // Read heat map of the most-read files for the HTML report; built during analysis, not stored in analysis.json.
    [JsonIgnore] public List<HeatMap>? HeatMaps { get; set; }
}
public sealed class HeatMap
{
    public string Path { get; set; } = "";
    public long MaxOffset { get; set; }
    public long[] Cells { get; set; } = [];
}

public sealed class DatasetManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string SessionId { get; set; } = "";
    public string GenerationId { get; set; } = Guid.NewGuid().ToString("N");
    public int Seed { get; set; } = 42;
    public string State { get; set; } = "Preparing";
    // The recording (or benchmark) the dummy was generated from, and the outcome files of runs since generation or the last
    // reset: together they tell which ranges a reset has to rewrite.
    public string? Session { get; set; }
    public List<string> Runs { get; set; } = [];
    // Which operations the dummy was made for (All / ProcessOnly / RootOnly), and how many recorded paths were left out.
    public string Scope { get; set; } = "All";
    public int ExcludedPaths { get; set; }
    public Dictionary<string,string> Paths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string,long> InitialSizes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Directories { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}

public sealed class ReplayOptions
{
    // ThinkTime by default: like the application, each thread waits for its request and then for the recorded gap, so a
    // slower or faster test target makes the whole run longer or shorter. Timeline keeps the recorded times instead.
    public ReplayMode Mode { get; set; } = ReplayMode.ThinkTime;
    public double Speed { get; set; } = 1;
    public int Workers { get; set; } = 8;
    public bool ProcessOnly { get; set; }
    public bool RootOnly { get; set; }
    public PagingReplay Paging { get; set; } = PagingReplay.UnmatchedReads;
    public bool TrimLeadingIdle { get; set; }
    // Wait before the first request until the OS has written recent writes out of its cache (WriteBack.SettleAsync).
    public bool WaitForWriteBack { get; set; } = true;
}

public sealed class ReplayOutcome
{
    public long Sequence { get; set; }
    public IoOperation Operation { get; set; }
    public string Path { get; set; } = "";
    public long PlannedUs { get; set; }
    public long ActualUs { get; set; }
    public double DurationMs { get; set; }
    public long Transferred { get; set; }
    public string Status { get; set; } = "";
    public string? Error { get; set; }
}
public sealed class ReplaySummary
{
    public string SessionId { get; set; } = "";
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public double ElapsedSeconds { get; set; }
    // Executed: requests sent to the test target, including recorded failures replayed as such.
    public long Executed { get; set; }
    public long Failed { get; set; }
    public long Skipped { get; set; }
    // Recorded failures sent again (a lookup of a missing name, a folder opened as a file, a read past the end, the end of a
    // folder listing): failed the same way / did not fail on the dummy.
    public long ReproducedFailures { get; set; }
    public long FailuresNotReproduced { get; set; }
    // Requests on a file object whose open was not recorded (opened before the recording, or by the system): the replay
    // opened the file for that request only, which the original did not do then.
    public long TemporaryOpens { get; set; }
    public long ReadBytes { get; set; }
    public long WriteBytes { get; set; }
    public double? P95Ms { get; set; }
    public double? P99Ms { get; set; }
    public double MaxSchedulingLagMs { get; set; }
    // How much later than planned the requests were issued: Timeline against the recorded times, ThinkTime against the
    // recorded gap after the same thread's previous request. Small values mean the recorded pacing was reproduced.
    public double? PacingErrorP50Ms { get; set; }
    public double? PacingErrorP95Ms { get; set; }
    public bool Cancelled { get; set; }
    public string? OutcomeFile { get; set; }
    // The recording of this replay (ReplayCapture), relative to the original record; null when it was not recorded.
    public string? TraceFolder { get; set; }
    public ReplayOptions Options { get; set; } = new();
    public List<string> Notes { get; set; } = [];
}

public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
    public static readonly JsonSerializerOptions Pretty = new(Options) { WriteIndented = true };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"JSONが空です: {path}");
    public static void Write<T>(string path, T data)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, Pretty));
        File.Move(temp, path, true);
    }
    // One JSON value per line, from a plain file or its compressed copy (<path>.gz, made by LogCompressor; also read when
    // the plain file was compressed in the meantime). tornTail: a last line cut off by a crash (no complete value) ends the
    // file instead of failing it; any other broken line fails.
    public static IEnumerable<T> Lines<T>(string path, bool tornTail = false)
    {
        if (!File.Exists(path))
        {
            if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && File.Exists(path + ".gz")) path += ".gz";
            else yield break;
        }
        using var reader = Open(path);
        if (reader is null) yield break;
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.Trim('\0').Length == 0) continue;
            T? item;
            try { item = JsonSerializer.Deserialize<T>(line, Options); }
            catch (JsonException ex)
            {
                if (tornTail && reader.Peek() < 0) yield break;
                throw new InvalidDataException($"壊れた記録が見つかりました: {path}", ex);
            }
            if (item is not null) yield return item;
        }
    }
    // Shared for writing and deleting: a plain file is read while it is still written, or while it is compressed and removed.
    private static StreamReader? Open(string path)
    {
        FileStream file;
        try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan); }
        catch (FileNotFoundException) when (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && File.Exists(path + ".gz"))
        { file = new FileStream(path + ".gz", FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.SequentialScan); path += ".gz"; }
        catch (FileNotFoundException) { return null; }
        Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress) : file;
        return new StreamReader(stream, new System.Text.UTF8Encoding(false), true, 1 << 16);
    }
}
