using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace IoRecorder.Core;

// Logarithmic histogram: reported percentiles are upper bounds within 5% (minimum 1 us).
public sealed class LatencyHistogram
{
    private readonly SortedDictionary<int,long> _buckets = [];
    public long Count { get; private set; }
    public void Add(double milliseconds)
    {
        var bucket = milliseconds <= 0 ? -1 : (int)Math.Ceiling(Math.Log(Math.Max(0.001, milliseconds) / 0.001, 1.05));
        _buckets[bucket] = _buckets.GetValueOrDefault(bucket) + 1;
        Count++;
    }
    public double? Percentile(double percentile)
    {
        if (Count == 0) return null;
        long threshold = Math.Max(1, (long)Math.Ceiling(Count * percentile)), count = 0;
        foreach (var (bucket, number) in _buckets)
        {
            count += number;
            if (count >= threshold) return bucket < 0 ? 0 : 0.001 * Math.Pow(1.05, bucket);
        }
        return null;
    }
}

public sealed class LiveMetrics
{
    private readonly object _gate = new();
    private readonly SortedDictionary<int,SecondMetrics> _seconds = [];
    private readonly Dictionary<string,FileMetrics> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly LatencyHistogram _latency = new();
    // Per-second Read / Write latency; a second's percentiles are recomputed only after it gets a new sample.
    private readonly Dictionary<int,(LatencyHistogram Read,LatencyHistogram Write)> _secondLatency = [];
    private readonly HashSet<int> _latencyChanged = [];
    private readonly Dictionary<string,LatencyHistogram> _fileLatency = new(StringComparer.OrdinalIgnoreCase);
    private long _events, _reads, _writes, _readBytes, _writeBytes, _failed, _fastIoRefusals, _otherProcess, _incomplete;
    private long _pagingReads, _pagingWrites, _pagingReadBytes, _pagingWriteBytes;
    private long _endUs, _firstIoUs = long.MaxValue, _lastIoEndUs;
    private bool _flags;

    public void Add(IoEvent e)
    {
        lock (_gate)
        {
            _events++;
            _endUs = Math.Max(_endUs, e.EndUs ?? e.StartUs);
            if (e.IsFastIoRefusal) { _fastIoRefusals++; return; }
            // Another process's I/O on the target's files: only its paging I/O (the OS moving the files' data between the
            // disk and its cache) belongs to the totals, in the paging figures.
            bool other = e.ByOtherProcess;
            if (other) _otherProcess++;
            else if (e.Failed) _failed++;
            // A change-notification request stays pending until something changes; it has no completion to wait for.
            if (e.IsFileIo && !e.EndUs.HasValue && !other && e.Operation != IoOperation.DirectoryNotify) _incomplete++;
            int second = (int)Math.Max(0, e.StartUs / 1_000_000);
            if (!_seconds.TryGetValue(second, out var s)) _seconds[second] = s = new() { Second = second };
            if (e.Operation == IoOperation.HardFault) { s.HardFaults++; return; }
            if (e.Operation == IoOperation.DiskRead) { s.DiskReadBytes += e.Size; return; }
            if (e.Operation == IoOperation.DiskWrite) { s.DiskWriteBytes += e.Size; return; }
            if (e.Operation is IoOperation.MapView or IoOperation.UnmapView) return;
            if (other && !(e.IsPagingIo && e.Operation is IoOperation.Read or IoOperation.Write)) return;
            if (!_files.TryGetValue(e.Path, out var f)) _files[e.Path] = f = new() { Path = e.Path };
            // Bandwidth uses completed byte counts when present; unknown completions use requested sizes.
            long bytes = e.Failed ? 0 : Math.Max(0, e.Transferred ?? e.Size);
            _flags |= e.IoFlags.HasValue;
            if (e.Operation == IoOperation.Read && e.Offset >= 0) f.MaxReadEnd = Math.Max(f.MaxReadEnd, e.Offset + bytes);
            // Paging I/O repeats or follows application requests at the cache layer; it never enters the application totals or latency.
            if (e.IsPagingIo && e.Operation is IoOperation.Read or IoOperation.Write)
            {
                if (e.Operation == IoOperation.Read) { _pagingReads++; _pagingReadBytes += bytes; s.PagingReadBytes += bytes; f.PagingReadBytes += bytes; }
                else { _pagingWrites++; _pagingWriteBytes += bytes; s.PagingWriteBytes += bytes; f.PagingWriteBytes += bytes; }
                return;
            }
            if (e.Operation is IoOperation.Read or IoOperation.Write)
            { _firstIoUs = Math.Min(_firstIoUs, e.StartUs); _lastIoEndUs = Math.Max(_lastIoEndUs, e.EndUs ?? e.StartUs); }
            if (e.Operation == IoOperation.Read)
            {
                _reads++; s.Reads++; f.Reads++;
                _readBytes += bytes; s.ReadBytes += bytes; f.ReadBytes += bytes;
                if (f.LastReadEnd == e.Offset) f.SequentialReads++;
                f.LastReadEnd = e.Offset + bytes;
            }
            else if (e.Operation == IoOperation.Write)
            {
                _writes++; s.Writes++; f.Writes++;
                _writeBytes += bytes; s.WriteBytes += bytes; f.WriteBytes += bytes;
            }
            else { s.Metadata++; f.Metadata++; }
            if (!e.Failed && e.Operation is IoOperation.Read or IoOperation.Write && e.DurationMs is { } latency)
            {
                _latency.Add(latency);
                if (!_fileLatency.TryGetValue(e.Path, out var fileLatency)) _fileLatency[e.Path] = fileLatency = new();
                fileLatency.Add(latency);
                if (!_secondLatency.TryGetValue(second, out var h)) _secondLatency[second] = h = (new(), new());
                (e.Operation == IoOperation.Read ? h.Read : h.Write).Add(latency);
                _latencyChanged.Add(second);
            }
        }
    }
    public AnalysisResult Snapshot(int maxFiles=500)
    {
        lock (_gate)
        {
            foreach (var second in _latencyChanged)
            {
                var (read, write) = _secondLatency[second]; var s = _seconds[second];
                s.ReadP50Ms = read.Percentile(.5); s.ReadP95Ms = read.Percentile(.95);
                s.WriteP50Ms = write.Percentile(.5); s.WriteP95Ms = write.Percentile(.95);
            }
            _latencyChanged.Clear();
            return new()
            {
                Version = AnalysisResult.CurrentVersion, TotalEvents = _events, FileCount = _files.Count, DurationSeconds = _endUs / 1_000_000.0,
                Reads = _reads, Writes = _writes, ReadBytes = _readBytes, WriteBytes = _writeBytes, IoFlagsAvailable = _flags,
                PagingReads = _pagingReads, PagingWrites = _pagingWrites, PagingReadBytes = _pagingReadBytes, PagingWriteBytes = _pagingWriteBytes,
                FirstIoUs = _firstIoUs == long.MaxValue ? null : _firstIoUs, LastIoEndUs = _firstIoUs == long.MaxValue ? null : _lastIoEndUs,
                FailedOperations = _failed, FastIoRefusals = _fastIoRefusals, OtherProcessEvents = _otherProcess, MissingCompletions = _incomplete, LatencySamples = _latency.Count,
                P50Ms = _latency.Percentile(.5), P95Ms = _latency.Percentile(.95), P99Ms = _latency.Percentile(.99),
                Timeline = _seconds.Values.Select(s => new SecondMetrics { Second=s.Second, Reads=s.Reads, Writes=s.Writes, ReadBytes=s.ReadBytes, WriteBytes=s.WriteBytes, Metadata=s.Metadata, HardFaults=s.HardFaults, DiskReadBytes=s.DiskReadBytes, DiskWriteBytes=s.DiskWriteBytes, PagingReadBytes=s.PagingReadBytes, PagingWriteBytes=s.PagingWriteBytes, ReadP50Ms=s.ReadP50Ms, ReadP95Ms=s.ReadP95Ms, WriteP50Ms=s.WriteP50Ms, WriteP95Ms=s.WriteP95Ms }).ToList(),
                Files = _files.Values.OrderByDescending(f => f.ReadBytes + f.WriteBytes).Take(maxFiles).Select(f => new FileMetrics { Path=f.Path, Reads=f.Reads, Writes=f.Writes, ReadBytes=f.ReadBytes, WriteBytes=f.WriteBytes, Metadata=f.Metadata, SequentialReads=f.SequentialReads, PagingReadBytes=f.PagingReadBytes, PagingWriteBytes=f.PagingWriteBytes, MaxReadEnd=f.MaxReadEnd,
                    P50Ms=_fileLatency.GetValueOrDefault(f.Path)?.Percentile(.5), P95Ms=_fileLatency.GetValueOrDefault(f.Path)?.Percentile(.95) }).ToList()
            };
        }
    }
}

public static class SessionAnalyzer
{
    public const string PagingSuffix = "（ページングI/O）";
    // The name an operation is counted under: reads and writes the OS does on its own (filling its cache, writing it back)
    // are counted apart from the application's, since they depend on the cache rather than on what the application asked.
    public static string OperationName(IoEvent e) =>
        e.IsPagingIo && e.Operation is IoOperation.Read or IoOperation.Write ? e.Operation + PagingSuffix : e.Operation.ToString();

    // Reads events.jsonl twice: totals and file states first, then ranges (which need the first mutation of each file)
    // and, when asked, the read heat map of the most-read files.
    // locations: only the I/O inside these folders (LocationFilter); null or empty for the whole record.
    public static AnalysisResult Analyze(string session, bool heat = false, LocationFilter? locations = null)
    {
        var manifest = JsonStore.Read<SessionManifest>(Path.Combine(session, "session.json"));
        if (manifest.SchemaVersion is < 1 or > SessionManifest.CurrentSchema) throw new InvalidDataException("未対応の記録形式です。");
        var metrics = new LiveMetrics();
        var files = new Dictionary<string,FileEntry>(StringComparer.OrdinalIgnoreCase);
        var first = new Dictionary<string,IoEvent>(StringComparer.OrdinalIgnoreCase);
        var firstOpen = new Dictionary<string,IoEvent>(StringComparer.OrdinalIgnoreCase);
        var notFound = new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
        var succeeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inFolder = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mutations = new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
        var renamedInto = new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
        var operations = new Dictionary<string,long>();
        var sizes = new Dictionary<string,long>();
        var extensions = new Dictionary<string,long>();
        bool narrowed = locations is { IsEmpty: false };
        foreach (var e in RecordLog.Read<IoEvent>(session, RecordLog.Events))
        {
            if (narrowed && !locations!.Includes(e.Path)) continue;
            metrics.Add(e);
            if (!e.IsFileIo || e.IsFastIoRefusal || e.ByOtherProcess) continue;
            if (!e.Failed && e.Operation==IoOperation.Rename && !string.IsNullOrWhiteSpace(e.DestinationPath))
                renamedInto[e.DestinationPath]=Math.Min(renamedInto.GetValueOrDefault(e.DestinationPath,long.MaxValue),e.StartUs);
            var op = OperationName(e); operations[op] = operations.GetValueOrDefault(op) + 1;
            if (!string.IsNullOrWhiteSpace(e.Path))
            {
                if (!first.TryGetValue(e.Path, out var f) || e.StartUs < f.StartUs) first[e.Path] = e;
                if (e.IsDirectory) directories.Add(e.Path);
                // STATUS_OBJECT_PATH_NOT_FOUND / STATUS_NOT_A_DIRECTORY / STATUS_OBJECT_NAME_INVALID: the path did not lead through
                // folders (it may be below a file, as in "a.psd\x"); anything else means its parent was a folder.
                if (!e.Failed || e.NtStatus is not (0xC000003A or 0xC0000103 or 0xC0000033)) inFolder.Add(e.Path);
                // STATUS_OBJECT_NAME_NOT_FOUND / STATUS_OBJECT_PATH_NOT_FOUND: an existence check before the file is created.
                if (e.Failed) { if (e.NtStatus is 0xC0000034 or 0xC000003A) notFound[e.Path] = Math.Min(notFound.GetValueOrDefault(e.Path, long.MaxValue), e.StartUs); }
                else
                {
                    succeeded.Add(e.Path);
                    if (e.Operation == IoOperation.Open && (!firstOpen.TryGetValue(e.Path, out var o) || e.StartUs < o.StartUs)) firstOpen[e.Path] = e;
                }
                if (!e.Failed && e.Operation is IoOperation.Write or IoOperation.SetEndOfFile or IoOperation.Delete or IoOperation.Rename)
                    mutations[e.Path] = Math.Min(mutations.GetValueOrDefault(e.Path, long.MaxValue), e.StartUs);
            }
            if (e.Operation is IoOperation.Read or IoOperation.Write && !e.IsPagingIo)
            {
                var size = e.Size switch { <= 4096 => "≤4 KiB", <= 16384 => "4–16 KiB", <= 65536 => "16–64 KiB", <= 262144 => "64–256 KiB", <= 1048576 => "256 KiB–1 MiB", _ => ">1 MiB" };
                sizes[size] = sizes.GetValueOrDefault(size) + 1;
                var ext = Path.GetExtension(e.Path).ToLowerInvariant(); if (ext == "") ext = "(拡張子なし)";
                extensions[ext] = extensions.GetValueOrDefault(ext) + (e.Failed ? 0 : e.Transferred ?? e.Size);
            }
        }
        foreach (var (path, e) in first)
        {
            // The first successful open tells whether the name existed: FILE_CREATED (2) means it did not.
            // Without any successful access, a not-found lookup means the name never existed during the trace.
            bool created, lookedUpFirst = notFound.TryGetValue(path, out var missingAt);
            if (firstOpen.TryGetValue(path, out var open))
            {
                created = open.ExtraInfo == 2 || open.ExtraInfo is null && open.CreateDisposition == 2;
                lookedUpFirst &= missingAt < open.StartUs;
            }
            else created = lookedUpFirst && !succeeded.Contains(path);
            files[path] = new()
            {
                Path=path, FirstUs=e.StartUs, FirstOperation=e.Operation, InitiallyExists=!created,
                IsDirectory=directories.Contains(path), SizeConfidence=!created ? "Unknown" : !succeeded.Contains(path) ? "NotFoundOnly" : lookedUpFirst ? "NotFoundBeforeCreate" : "CreatedDuringTrace",
                Modified=mutations.ContainsKey(path)
            };
        }
        foreach (var s in JsonStore.Lines<SizeObservation>(Path.Combine(session, "sizes.jsonl")))
            if (files.TryGetValue(s.Path, out var f))
            { f.ObservedSize=s.Size; f.ObservedUs=s.ObservedUs; f.IsDirectory |= s.IsDirectory; }
        // A path that is the parent of another accessed path is a directory, whatever its own events said.
        foreach (var path in files.Keys.Where(inFolder.Contains))
            for (var parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                if (files.TryGetValue(parent, out var p)) { if (p.IsDirectory) break; p.IsDirectory = true; }
        var result = metrics.Snapshot(int.MaxValue);
        result.DurationSeconds = Math.Max(result.DurationSeconds, ((manifest.EndedUtc ?? manifest.StartedUtc) - manifest.StartedUtc).TotalSeconds);
        result.Locations = narrowed ? [.. locations!.Folders] : [];
        Dictionary<string,HeatMap>? heats = null;
        if (heat)
        {
            heats = result.Files.Where(f => f.Reads > 0).OrderByDescending(f => f.ReadBytes).Take(12)
                .ToDictionary(f => f.Path, f => new HeatMap { Path = f.Path, MaxOffset = Math.Max(1, Math.Max(files.GetValueOrDefault(f.Path)?.ObservedSize ?? 0, f.MaxReadEnd)), Cells = new long[64 * 120] }, StringComparer.OrdinalIgnoreCase);
            result.HeatMaps = [.. heats.Values];
        }
        // A bounded interval union provides a conservative unique-read footprint; it is never sold as LucidLink cache demand.
        var ranges = new Dictionary<string,List<(long Start,long End)>>(StringComparer.OrdinalIgnoreCase);
        int intervalCount = 0; bool capped = false;
        var patterns = new AccessPatternBuilder(files);
        foreach (var e in RecordLog.Read<IoEvent>(session, RecordLog.Events))
        {
            if (e.Operation is not (IoOperation.Read or IoOperation.Write) || e.Failed || e.ByOtherProcess || e.Offset < 0 || !files.TryGetValue(e.Path, out var f)) continue;
            var count = Math.Max(0, e.Transferred ?? e.Size);
            if (count > long.MaxValue - e.Offset) continue;
            if (!e.IsPagingIo) patterns.Add(e, count);
            if (e.Operation == IoOperation.Write) continue;
            if (heats is not null && !e.IsPagingIo && heats.TryGetValue(e.Path, out var h))
            {
                int x = Math.Clamp((int)(e.StartUs / 1_000_000.0 / Math.Max(1, result.DurationSeconds) * 120), 0, 119);
                int y = Math.Clamp((int)(e.Offset / (double)h.MaxOffset * 64), 0, 63);
                h.Cells[y * 120 + x] += count;
            }
            long end = e.Offset + count;
            if (e.StartUs < mutations.GetValueOrDefault(e.Path, long.MaxValue)) f.MinimumInitialReadExtent = Math.Max(f.MinimumInitialReadExtent, end);
            if (intervalCount >= 100_000) { capped = true; continue; }
            if (!ranges.TryGetValue(e.Path, out var list)) ranges[e.Path] = list = [];
            int index = list.BinarySearch((e.Offset,end), Comparer<(long Start,long End)>.Create((a,b) => a.Start.CompareTo(b.Start)));
            if (index < 0) index = ~index;
            long start = e.Offset;
            if (index > 0 && list[index-1].End >= start) { index--; start = Math.Min(start,list[index].Start); }
            while (index < list.Count && list[index].Start <= end)
            { start = Math.Min(start,list[index].Start); end = Math.Max(end,list[index].End); list.RemoveAt(index); intervalCount--; }
            list.Insert(index,(start,end)); intervalCount++;
        }
        foreach (var f in files.Values)
        {
            if (renamedInto.TryGetValue(f.Path,out var renamedAt) && f.FirstUs>=renamedAt)
            { f.InitiallyExists=false; f.SizeConfidence="RenamedDuringTrace"; }
            if (!f.InitiallyExists) { f.SuggestedInitialSize=0; continue; }
            f.SuggestedInitialSize = Math.Max(f.ObservedSize ?? 0, f.MinimumInitialReadExtent);
            f.SizeConfidence = f.ObservedSize.HasValue ? "ObservedAfterEvent_NotInitialSnapshot" : f.MinimumInitialReadExtent > 0 ? "ReadExtentLowerBound" : "Unknown";
        }
        result.Operations=operations; result.SizeDistribution=sizes; result.ExtensionBytes=extensions;
        result.FileManifest=files.Values.OrderBy(f=>f.FirstUs).ToList();
        result.UniqueReadBytesLowerBound=ranges.Values.Sum(list=>list.Sum(r=>r.End-r.Start)); result.UniqueReadCapped=capped;
        (result.AccessPatterns, result.ChunkEstimates, result.AccessRangesCapped) = patterns.Build(files);
        return result;
    }

    // Summary files for a session: analysis.json, file-manifest.json, timeline.csv and report.html, from two reads of the
    // events. The per-event CSV (as large as the record) is written only on request: ExportCsv.
    public static AnalysisResult Export(string session, IProgress<string>? progress = null)
    {
        progress?.Report("記録を集計しています…");
        var result = Analyze(session, heat: true);
        Save(session, result);
        progress?.Report("HTMLレポートとファイル一覧を保存しました。");
        return result;
    }
    private static void Save(string session, AnalysisResult result)
    {
        JsonStore.Write(Path.Combine(session,"analysis.json"),result);
        JsonStore.Write(Path.Combine(session,"file-manifest.json"),result.FileManifest);
        var bySecond = result.Timeline.ToDictionary(s => s.Second);
        static string Ms(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        using (var writer = new StreamWriter(Path.Combine(session,"timeline.csv"),false,new UTF8Encoding(true)))
        {
            writer.WriteLine("second,read_iops,write_iops,read_bytes,write_bytes,metadata_ops,hard_faults,disk_read_bytes,disk_write_bytes,paging_read_bytes,paging_write_bytes,read_p50_ms,read_p95_ms,write_p50_ms,write_p95_ms");
            for (int second=0; second<=Math.Ceiling(result.DurationSeconds); second++)
            {
                var s=bySecond.GetValueOrDefault(second) ?? new SecondMetrics { Second=second };
                writer.WriteLine($"{s.Second},{s.Reads},{s.Writes},{s.ReadBytes},{s.WriteBytes},{s.Metadata},{s.HardFaults},{s.DiskReadBytes},{s.DiskWriteBytes},{s.PagingReadBytes},{s.PagingWriteBytes},{Ms(s.ReadP50Ms)},{Ms(s.ReadP95Ms)},{Ms(s.WriteP50Ms)},{Ms(s.WriteP95Ms)}");
            }
        }
        using (var writer = new StreamWriter(Path.Combine(session,"access-patterns.csv"),false,new UTF8Encoding(true)))
        {
            static string R(double? value) => value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";
            writer.WriteLine("path,shape,size_bytes,reads,read_bytes,unique_read_bytes,read_coverage,reread_factor,read_size_p50_bytes,read_size_p95_bytes,sequential_ratio,near_ratio,jump_p50_bytes,write_shape,writes,write_bytes,unique_write_bytes,write_size_p50_bytes,write_continuous_ratio,existed_at_start,initial_size_bytes,existing_overwrite_bytes,"
                + string.Join(",", AccessPatternBuilder.ChunkSizes.Select(c => $"fetch_bytes_{c / 1024}k")));
            foreach (var p in result.AccessPatterns)
                writer.WriteLine(string.Join(',', Csv(p.Path), Csv(p.Shape), p.SizeBytes, p.Reads, p.ReadBytes, p.UniqueReadBytes, R(p.ReadCoverage), R(p.RereadFactor), p.ReadSizeP50Bytes, p.ReadSizeP95Bytes,
                    R(p.SequentialRatio), R(p.NearRatio), p.JumpP50Bytes, Csv(p.WriteShape), p.Writes, p.WriteBytes, p.UniqueWriteBytes, p.WriteSizeP50Bytes, R(p.WriteContinuousRatio),
                    p.ExistedAtStart, p.InitialSizeBytes, p.ExistingOverwriteBytes, string.Join(',', p.FetchBytes)));
        }
        HtmlReport.Write(session,result);
    }

    // The summary saved by Export when it is newer than the record and of the current version. Otherwise the record is
    // analyzed again and the summary files are rewritten, so the next open is quick and the report has the current charts.
    // locations: the record narrowed to these folders; its summary is kept apart (NarrowedAnalysis) and the whole record's
    // summary files stay as they are.
    public static AnalysisResult Load(string session, IProgress<string>? progress = null, LocationFilter? locations = null)
    {
        // A record cut off by a crash is finished first (RecordRecovery), which also makes it newer than its summary.
        if (RecordRecovery.Prepare(session)) progress?.Report("途中で終わっていた記録を、書き出されていた所までで確定しました。");
        string analysis = Path.Combine(session, "analysis.json"), sizes = Path.Combine(session, "sizes.jsonl");
        DateTime newest = new[] { sizes, Path.Combine(session, "session.json") }.Where(File.Exists).Select(File.GetLastWriteTimeUtc)
            .Append(RecordLog.LastWriteUtc(session, RecordLog.Events)).Max();
        if (locations is { IsEmpty: false })
        {
            string narrowed = Path.Combine(session, NarrowedAnalysis.FileName);
            if (File.Exists(narrowed) && File.GetLastWriteTimeUtc(narrowed) >= newest)
                try
                {
                    var saved = JsonStore.Read<NarrowedAnalysis>(narrowed);
                    if (saved.Key == locations.Key && saved.Result.Version >= AnalysisResult.CurrentVersion) return saved.Result;
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException) { }
            progress?.Report($"場所を絞り込んで集計しています（{locations.Describe()}）…");
            var part = Analyze(session, heat: true, locations);
            try
            {
                JsonStore.Write(narrowed, new NarrowedAnalysis { Key = locations.Key, Result = part });
                HtmlReport.Write(session, part, NarrowedAnalysis.ReportName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return part;
        }
        if (File.Exists(analysis) && File.GetLastWriteTimeUtc(analysis) >= newest)
            try
            {
                var saved = JsonStore.Read<AnalysisResult>(analysis);
                if (saved.Version >= AnalysisResult.CurrentVersion) return saved;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { }
        progress?.Report("記録を集計し直しています（集計が古い形式か、記録より古いため）…");
        var result = Analyze(session, heat: true);
        // A record on read-only storage is still shown; only the saved summary is skipped.
        try { Save(session, result); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }

    public static string ExportCsv(string session, IProgress<string>? progress = null)
    {
        progress?.Report("events.csv を書き出しています…");
        string path = Path.Combine(session, "events.csv");
        using (var writer = new StreamWriter(path + ".tmp", false, new UTF8Encoding(true)))
        {
            writer.WriteLine("sequence,start_us,end_us,pid,tid,process,operation,path,destination,offset,requested_bytes,transferred_bytes,ntstatus,info_class,extra_info,io_flags,paging_io,process_match,root_match");
            foreach (var e in RecordLog.Read<IoEvent>(session, RecordLog.Events))
                writer.WriteLine(string.Join(',', e.Sequence, e.StartUs, e.EndUs?.ToString() ?? "", e.ProcessId, e.ThreadId, Csv(e.ProcessName), e.Operation, Csv(e.Path), Csv(e.DestinationPath ?? ""), e.Offset, e.Size, e.Transferred?.ToString() ?? "", e.NtStatus is { } st ? $"0x{st:X8}" : "", e.InfoClass?.ToString() ?? "", e.ExtraInfo?.ToString() ?? "", e.IoFlags is { } fl ? $"0x{fl:X}" : "", e.IsPagingIo, e.MatchesProcess, e.MatchesRoot));
        }
        File.Move(path + ".tmp", path, true);
        progress?.Report("events.csv を書き出しました: " + path);
        return path;
    }
    private static string Csv(string value)
    {
        if (value.Length>0 && "=+-@\t\r".Contains(value[0])) value="'"+value;
        return "\"" + value.Replace("\"","\"\"") + "\"";
    }
}

public static class HtmlReport
{
    public static void Write(string session, AnalysisResult a, string fileName = "report.html")
    {
        var m=JsonStore.Read<SessionManifest>(Path.Combine(session,"session.json"));
        // The heat map comes from Analyze(heat: true); the report itself never reads the events again.
        var heats=a.HeatMaps ?? [];
        string E(string value)=>WebUtility.HtmlEncode(value);
        string N(double value)=>value.ToString("N1",CultureInfo.InvariantCulture);
        string P(double? value)=>value.HasValue?N(value.Value)+" ms":"未取得";
        var rows=string.Join("",a.Files.Take(100).Select(f=>$"<tr><td title=\"{E(f.Path)}\">{E(f.Path)}</td><td>{f.Reads:N0}</td><td>{f.Writes:N0}</td><td>{N(f.ReadBytes/1048576.0)}</td><td>{N(f.WriteBytes/1048576.0)}</td></tr>"));
        // Read patterns per file and the chunk-size estimate.
        static string B(long bytes)=>bytes>=1L<<30?(bytes/1073741824.0).ToString("N2",CultureInfo.InvariantCulture)+" GiB":bytes>=1L<<20?(bytes/1048576.0).ToString("N1",CultureInfo.InvariantCulture)+" MiB":(bytes/1024.0).ToString("N0",CultureInfo.InvariantCulture)+" KiB";
        static string Pct(double? value)=>value.HasValue?(value.Value*100).ToString("N0",CultureInfo.InvariantCulture)+"%":"—";
        // Efficiency = what was really used of what was fetched or uploaded (the inverse of the amplification).
        static string Eff(double? amplification)=>amplification is > 0?(100/amplification.Value).ToString("N1",CultureInfo.InvariantCulture)+"%":"—";
        static string Repeat(long total,long unique){ long repeat=Math.Max(0,total-unique); return repeat==0?"0":$"{B(repeat)}（{Pct(repeat/(double)Math.Max(1,total))}）"; }
        long readTotal=Math.Max(1,a.AccessPatterns.Sum(p=>p.ReadBytes));
        var shapeRows=string.Join("",a.AccessPatterns.Where(p=>p.Reads>0).GroupBy(p=>p.Shape).OrderByDescending(g=>g.Sum(p=>p.ReadBytes))
            .Select(g=>$"<tr><td>{E(g.Key)}</td><td>{g.Count():N0}</td><td>{B(g.Sum(p=>p.ReadBytes))}</td><td>{Pct(g.Sum(p=>p.ReadBytes)/(double)readTotal)}</td></tr>"));
        // Reads: 読み取り量 = 再読み取り量 + 読み取り範囲, the range against the file size (the coverage).
        static string RangeOfSize(FileAccessPattern p)=>p.SizeBytes>0?$"{B(p.UniqueReadBytes)} / {B(p.SizeBytes)}（{Pct(p.ReadCoverage)}）":B(p.UniqueReadBytes);
        var patternRows=string.Join("",a.AccessPatterns.Where(p=>p.Reads>0).Take(100).Select(p=>$"<tr><td title=\"{E(p.Path)}\">{E(p.Path)}</td><td>{E(p.Shape)}</td><td>{B(p.ReadBytes)}</td>"
            +$"<td title=\"同じ場所を平均{(p.RereadFactor??1).ToString("N1",CultureInfo.InvariantCulture)}回読んでいます\">{Repeat(p.ReadBytes,p.UniqueReadBytes)}</td><td>{RangeOfSize(p)}</td>"
            +$"<td>≤{B(p.ReadSizeP50Bytes)} / ≤{B(p.ReadSizeP95Bytes)}</td><td>{Pct(p.SequentialRatio+p.NearRatio)}</td><td>{string.Join(" / ",p.FetchBytes.Select(f=>Eff(p.UniqueReadBytes>0?(double)f/p.UniqueReadBytes:null)))}</td></tr>"));
        long writeTotal=Math.Max(1,a.AccessPatterns.Sum(p=>p.WriteBytes));
        var writeShapeRows=string.Join("",a.AccessPatterns.Where(p=>p.Writes>0).GroupBy(p=>p.WriteShape).OrderByDescending(g=>g.Sum(p=>p.WriteBytes))
            .Select(g=>$"<tr><td>{E(g.Key)}</td><td>{g.Count():N0}</td><td>{B(g.Sum(p=>p.WriteBytes))}</td><td>{Pct(g.Sum(p=>p.WriteBytes)/(double)writeTotal)}</td></tr>"));
        // Writes: 書き込み量 = 再書き込み量 + 既存の上書き + 新しい部分.
        static string Overwrite(FileAccessPattern p)=>!p.ExistedAtStart?"新規":p.InitialSizeBytes==0?"0 / 0（空のファイル）"
            :$"{(p.ExistingOverwriteBytes>0?B(p.ExistingOverwriteBytes):"0")} / {B(p.InitialSizeBytes)}（{Pct(p.ExistingOverwriteBytes/(double)p.InitialSizeBytes)}）";
        static string Added(FileAccessPattern p){ long added=Math.Max(0,p.UniqueWriteBytes-p.ExistingOverwriteBytes); return added>0?B(added):"0"; }
        var writeRows=string.Join("",a.AccessPatterns.Where(p=>p.Writes>0).OrderByDescending(p=>p.WriteBytes).Take(100).Select(p=>$"<tr><td title=\"{E(p.Path)}\">{E(p.Path)}</td><td>{E(p.WriteShape)}</td><td>{B(p.WriteBytes)}</td>"
            +$"<td>{Repeat(p.WriteBytes,p.UniqueWriteBytes)}</td><td>{Overwrite(p)}</td><td>{Added(p)}</td><td>≤{B(p.WriteSizeP50Bytes)}</td><td>{Pct(p.WriteContinuousRatio)}</td></tr>"));
        var chunkRows=string.Join("",a.ChunkEstimates.Select(c=>$"<tr><td>{B(c.ChunkBytes)}</td><td>{c.ReadChunks:N0}</td><td>{B(c.ReadFetchBytes)}</td><td>{Eff(c.ReadAmplification)}</td><td>{c.WriteChunks:N0}</td><td>{B(c.WriteUploadBytes)}</td><td>{B(c.WriteEachBytes)}</td><td>{Eff(c.WriteAmplification)}</td><td>{c.WritePartialChunks:N0}</td></tr>"));
        string chunkHeads=string.Join(" / ",AccessPatternBuilder.ChunkSizes.Select(c=>c>=1L<<20?$"{c>>20}M":$"{c>>10}K"));
        var extRows=string.Join("",a.ExtensionBytes.OrderByDescending(p=>p.Value).Select(p=>$"<tr><td>{E(p.Key)}</td><td>{N(p.Value/1048576.0)} MiB</td></tr>"));
        var sizeRows=string.Join("",a.SizeDistribution.Select(p=>$"<tr><td>{E(p.Key)}</td><td>{p.Value:N0}</td></tr>"));
        var warnings=string.Join("",m.Warnings.Select(w=>$"<li>{E(w)}</li>"));
        // Left-out processes that were busy on the same drives (counted without file names during the recording).
        string others=OtherProcessIo.Summary(m,a,5) is { } busy?$"<p>記録しなかったプロセスで、同じドライブへのI/Oが多かったもの（ファイル名は残していません）: {E(busy)}</p>":"";
        string html=$$$"""
<!doctype html><html lang="ja"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>IO Recorder — {{{E(m.Label)}}}</title>
<style>
:root{color-scheme:dark;font-family:Segoe UI,Meiryo,sans-serif;background:#0b101b;color:#e7edf7}*{box-sizing:border-box}body{max-width:1240px;margin:auto;padding:44px 28px}h1{font-size:32px;margin:8px 0}h2{font-size:19px}p,.muted{color:#9fadc5;line-height:1.8}.eyebrow{color:#69d8ce;letter-spacing:3px;font-size:12px}.cards{display:grid;grid-template-columns:repeat(4,1fr);gap:14px;margin:28px 0}.card,section{background:#131c2c;border:1px solid #26334a;border-radius:16px;padding:22px}.card b{display:block;font-size:29px;margin-top:8px}section{margin-bottom:20px}canvas{width:100%;height:260px;display:block}#timeline{height:600px}select{max-width:100%;background:#0b101b;color:#e7edf7;padding:10px;border:1px solid #32415c;border-radius:8px}table{width:100%;border-collapse:collapse;font-size:13px}td,th{text-align:right;border-bottom:1px solid #27344c;padding:12px 8px}td:first-child,th:first-child{text-align:left;max-width:650px;overflow-wrap:anywhere}th{color:#9fadc5}.grid{display:grid;grid-template-columns:1fr 1fr;gap:20px}.pill{display:inline-block;border:1px solid #31425f;border-radius:20px;padding:5px 13px;color:#69d8ce;margin-top:8px}#tip{height:24px;font-size:13px;color:#9fadc5}@media(max-width:750px){.cards,.grid{grid-template-columns:1fr 1fr}body{padding:20px 12px}.card b{font-size:23px}}
</style><h1>{{{E(m.Label.Length>0?m.Label:"ファイルI/O 計測レポート")}}}</h1>
<p>{{{E(m.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"))}}} · {{{E(m.Machine)}}} · {{{N(a.DurationSeconds)}}} 秒</p>
<span class="pill">{{{E(m.Options.Mode.ToString())}}} · {{{E(m.State)}}} · 内容データを含まない記録</span>{{{(a.Locations.Count>0?"<p>場所を絞り込んだ集計です（この中のI/Oだけ）: "+E(LocationFilter.TextOf(a.Locations))+"</p>":"")}}}
<div class="cards"><div class="card">Read（アプリ要求）<b>{{{N(a.ReadBytes/1048576.0)}}} <small>MiB</small></b><p>{{{a.Reads:N0}}} requests · ページング {{{(a.IoFlagsAvailable?N(a.PagingReadBytes/1048576.0)+" MiB":"判別不可")}}}</p></div><div class="card">Write（アプリ要求）<b>{{{N(a.WriteBytes/1048576.0)}}} <small>MiB</small></b><p>{{{a.Writes:N0}}} requests · ページング {{{(a.IoFlagsAvailable?N(a.PagingWriteBytes/1048576.0)+" MiB":"判別不可")}}}</p></div><div class="card">Read/Write P95<b>{{{P(a.P95Ms)}}}</b><p>P99 {{{P(a.P99Ms)}}}</p></div><div class="card">参照されたファイル<b>{{{a.FileManifest.Count:N0}}}</b><p>ユニークRead領域 ≥ {{{N(a.UniqueReadBytesLowerBound/1048576.0)}}} MiB</p></div></div>
<section><h2>時間ごとの推移</h2><p>青: Read ／ 緑: Write。アプリ要求のファイルI/Oを1秒ごとに集計し、3つのグラフは同じ時間軸です。レイテンシは、その秒に始まった成功Read/WriteのP95（対数目盛・I/Oがない秒は線が切れます）。カーソルを重ねると値を表示します。</p><canvas id="timeline"></canvas><div id="tip"></div></section>
<section><h2>ファイル内のReadアクセス</h2><p>横軸: 経過時間 ／ 縦軸: ファイル内の位置（観測サイズ、または最大Read位置まで）。色の明るさ: 読み込みバイト数。開始位置を64分割して集計しています。</p><select id="files"></select><canvas id="heat"></canvas><div id="heatTip"></div></section>
<section><h2>ファイルごとの読み方</h2><p>アプリの要求（ページングI/Oを除く）から、ファイルごとに「どこを、どの大きさで、どの順に」読んだかをまとめています。読み取り量 = 再読み取り量 + 読み取り範囲（重複を除く）。読み取り範囲はファイルサイズと並べ、括弧にカバー率（読み取り範囲 ÷ ファイルサイズ）を出しています。連続 = 同じハンドルの前の読み込みの続きから、または1 MiB以内の前方への読み飛ばしで読んだ割合。再読み取り量 = すでに一度読んだ場所をもう一度読んだ分で、キャッシュに残っていればそこから返っているかもしれない量（セルにカーソルを重ねると、平均何回読んだかが出ます）。読み取りサイズ（中央値 / P95）は1回の読み取りの大きさで、2のべき乗の区切りの上限値です。型: 連続が70%以上なら「全体を順に」（カバー率90%以上）か「一部を順に」、30%未満なら「散らばる（疎）」、その間は「混在」、読み込みが3回以下なら「数回だけ」。ファイルサイズはアクセスの後に照会した値か、読み書きした最も後ろの位置で、記録中に大きくなったファイルではカバー率がずれます。</p>
<table><tr><th>型</th><th>ファイル数</th><th>読み取り量</th><th>割合</th></tr>{{{shapeRows}}}</table>
<h2>読み込み量の多いファイル（上位100件）</h2><table><tr><th>パス</th><th>型</th><th>読み取り量</th><th>再読み取り量</th><th>読み取り範囲 / サイズ（カバー率）</th><th>読み取りサイズ</th><th>連続</th><th>取得の効率（{{{chunkHeads}}}）</th></tr>{{{patternRows}}}</table>{{{(a.AccessRangesCapped?"<p>範囲の集計が上限に達したため、読み取り範囲は下限値で、カバー率・再読み取り量・効率は目安です。</p>":"")}}}</section>
<section><h2>ファイルごとの書き方</h2><p>アプリの書き込み（ページングI/Oを除く）を、ファイルごとにまとめています。書き込み量 = 再書き込み量 + 既存の上書き + 新しい部分で、書いたバイトは必ずこの3つのどれかに入ります。再書き込み量 = すでに一度書いた場所をもう一度書いた分で、アップロード前にまとめられればアップロードが減り得る量です。既存の上書き = 記録の開始時点で既にあったデータへの1回目の書き込みで、開始時のサイズ（推定）と並べ、括弧に開始時のデータのうち上書きした割合を出しています。分割方式のストレージでは、断片の一部だけを書き換えると残りの内容が必要になります。新しい部分 = 開始時のサイズより後ろへの書き込み（末尾への追記や新規ファイルの中身）で、新しい断片としてそのまま上げる分です。型: 開始時にデータが無かったファイルは「新規に順に書く」か「新規（順不同）」、既存の上書きが無ければ「末尾に追記」、重複を除いた書き込みの9割以上が既存の上書きなら「既存を順に上書き」か「既存の一部を上書き」、その他は「混在」、書き込みが3回以下なら「数回だけ」。連続 = 同じハンドルの前の書き込みの続きから（1 MiB以内の読み飛ばしを含む）書いた割合。</p>
<table><tr><th>型</th><th>ファイル数</th><th>書き込み量</th><th>割合</th></tr>{{{writeShapeRows}}}</table>
<h2>書き込み量の多いファイル（上位100件）</h2><table><tr><th>パス</th><th>型</th><th>書き込み量</th><th>再書き込み量</th><th>既存の上書き / 開始時のサイズ</th><th>新しい部分</th><th>書き込みサイズ</th><th>連続</th></tr>{{{writeRows}}}</table></section>
<section><h2>分割単位ごとの取得量の試算</h2><p>LucidLinkのように、ファイルを同じ大きさの断片（チャンク）に分けて保存し、読まれた位置を含む断片を取りに行くストレージを想定した試算です。キャッシュが空・先読みなし・一度取った断片は取り直さない、という前提の下限値で、実際の転送量や待ち時間の予測ではありません。分割単位の候補を比べる材料として使います。取得回数は、キャッシュが空のときに取りに行く回数（キャッシュミスの回数）にあたります。効率 = 実際に読んだ範囲（重複を除く）÷ 取得量で、100%なら無駄なし、散らばった小さな読み込みが多いほど低くなります。書き込みは、更新される断片の数と、アップロード量の下限（記録全体で同じ断片を1回にまとめられた場合）と上限（書き込みのたびに触れた断片を送る場合）、効率 = 書いた範囲 ÷ 下限です。一部書き換え = 開始時にあったデータの断片で、書き込みが一部しか覆わないものの数。アップロードの前に残りの内容が必要で、キャッシュに無ければ取りに行きます（取得量には含めていません）。</p>
<table><tr><th>分割単位</th><th>読み込み: 取得回数</th><th>取得量</th><th>効率</th><th>書き込み: 更新回数</th><th>アップロード量（下限）</th><th>アップロード量（上限）</th><th>効率</th><th>一部書き換え</th></tr>{{{chunkRows}}}</table></section>
<div class="grid"><section><h2>拡張子別のRead + Write量</h2><table>{{{extRows}}}</table></section><section><h2>要求サイズの分布</h2><table>{{{sizeRows}}}</table></section></div>
<section><h2>アクセス量の多いファイル（上位100件）</h2><table><tr><th>パス</th><th>Read回数</th><th>Write回数</th><th>Read MiB</th><th>Write MiB</th></tr>{{{rows}}}</table></section>
<section><h2>記録の品質と測定範囲</h2><p>ETW欠落: {{{(m.EtwLossCountAvailable?m.EtwEventsLost.ToString("N0"):"未取得")}}} ／ 保存待ち行列の欠落: {{{m.QueueEventsDropped:N0}}} ／ 完了未取得: {{{a.MissingCompletions:N0}}} ／ 失敗した操作: {{{a.FailedOperations:N0}}} ／ 高速I/Oの拒否（すぐ出し直されたため数えない）: {{{a.FastIoRefusals:N0}}} ／ 対象のファイルへの他のプロセスのI/O（ページングI/O以外は集計に含めない）: {{{a.OtherProcessEvents:N0}}} ／ パス未解決: {{{m.UnresolvedPaths:N0}}}</p>{{{others}}}<ul>{{{warnings}}}</ul><p>レイテンシは開始と完了を対応付けた成功Read/Writeのみ（{{{a.LatencySamples:N0}}}件）。分位値は最大5%幅のヒストグラム上限値です。転送量は完了時のバイト数を優先し、完了不明の場合は要求サイズを使用します。{{{(a.IoFlagsAvailable?"Read/Write・レイテンシ・拡張子別・要求サイズ分布はアプリ要求（非ページング）のみで、キャッシュ補充・先読み・Lazy Writer・mmapのページングI/Oは別集計です。":"この記録にはIoFlagsがないため、ページングI/Oとアプリ要求を区別できず、両方を含む値です。")}}}ETWのファイルI/Oと物理ディスクI/Oは別集計です。クラウド転送量・mmap上の全参照・ロックAPIの引数はこの記録だけでは分かりません。ファイルサイズの照会値は非同期観測値で、初期サイズの確定値ではありません。ユニークRead領域は{{{(a.UniqueReadCapped?"集計上限に達した下限値":"観測範囲の区間和")}}}であり、LucidLinkの必要キャッシュ容量そのものではありません。</p></section>
<script>
const series={{{JsonSerializer.Serialize(a.Timeline,JsonStore.Options)}}},duration={{{a.DurationSeconds.ToString(CultureInfo.InvariantCulture)}}},heats={{{JsonSerializer.Serialize(heats,JsonStore.Options)}}};
function setup(id){const c=document.getElementById(id),d=devicePixelRatio||1;c.width=c.clientWidth*d;c.height=c.clientHeight*d;const x=c.getContext('2d');x.scale(d,d);return[c,x,c.clientWidth,c.clientHeight]}
// Three panels on one time axis: transfer, IOPS and P95 latency (log scale; seconds without I/O leave a gap).
const T=Math.max(1,Math.ceil(duration)),bySec=new Map(series.map(s=>[s.second,s])),colors=['#72a7ff','#66d9c7'];
const panels=[{title:'転送量（MiB/s）',keys:['readMiB','writeMiB']},{title:'IOPS（回/秒）',keys:['reads','writes']},{title:'レイテンシ P95（ms・対数目盛）',keys:['readP95Ms','writeP95Ms'],log:true}];
let cursor=null;
function nice(v){const m=Math.pow(10,Math.floor(Math.log10(v))),f=v/m;return(f<=1?1:f<=2?2:f<=2.5?2.5:f<=5?5:10)*m}
function fmt(v){return v>=100?v.toFixed(0):String(+v.toPrecision(2))}
function clock(t){const p=n=>String(n).padStart(2,'0'),hh=Math.floor(t/3600),mm=Math.floor(t%3600/60),ss=t%60;return T>=3600?hh+':'+p(mm)+':'+p(ss):T>=60?mm+':'+p(ss):t+' s'}
function timeline(){const[c,x,w,h]=setup('timeline');const L=58,R=w-14,axis=22,gap=18,ph=(h-axis-gap*2)/3,X=t=>L+t/T*(R-L);
x.font='12px Segoe UI';x.lineWidth=1;const step=[1,2,5,10,15,30,60,120,300,600,900,1800,3600,7200,14400].find(s=>T/s<=8)||14400;
panels.forEach((p,i)=>{const top=i*(ph+gap),y0=top+24,y1=top+ph;x.textAlign='left';x.fillStyle='#e7edf7';x.fillText(p.title,L,top+13);
let vals=[];for(let t=0;t<=T;t++){const s=bySec.get(t);for(const k of p.keys){const v=s?s[k]:null;if(v!=null&&(!p.log||v>0))vals.push(v)}}
let Y,ticks=[];if(p.log){if(!vals.length){x.fillStyle='#9fadc5';x.fillText('レイテンシの記録なし',L+12,y0+30);return}
let lo=Math.floor(Math.log10(vals.reduce((a,b)=>Math.min(a,b)))),hi=Math.ceil(Math.log10(vals.reduce((a,b)=>Math.max(a,b))));if(hi<=lo)hi=lo+1;
Y=v=>y1-(Math.log10(v)-lo)/(hi-lo)*(y1-y0);for(let k=lo;k<=hi;k++)ticks.push(Math.pow(10,k))}
else{const max=vals.reduce((a,b)=>Math.max(a,b),0),st=nice(Math.max(max,1e-6)/4);Y=v=>y1-v/(st*4)*(y1-y0);for(let k=0;k<=4;k++)ticks.push(st*k)}
x.strokeStyle='#28364f';x.fillStyle='#9fadc5';for(const v of ticks){const y=Y(v);x.beginPath();x.moveTo(L,y);x.lineTo(R,y);x.stroke();x.textAlign='right';x.fillText(fmt(v),L-8,y+4)}
x.strokeStyle='#1d2a3f';for(let t=step;t<T;t+=step){x.beginPath();x.moveTo(X(t),y0);x.lineTo(X(t),y1);x.stroke()}
p.keys.forEach((k,j)=>{x.strokeStyle=x.fillStyle=colors[j];x.lineWidth=2;x.beginPath();let pen=false;
for(let t=0;t<=T;t++){const s=bySec.get(t);let v=s?s[k]:null;if(p.log){if(v==null||v<=0){pen=false;continue}}else v=v||0;const px=X(t),py=Y(v);
if(pen)x.lineTo(px,py);else{x.moveTo(px,py);pen=true;const n=bySec.get(t+1);if(p.log&&!(n&&n[k]>0))x.fillRect(px-1.5,py-1.5,3,3)}}x.stroke();x.lineWidth=1})});
x.fillStyle='#9fadc5';for(let t=0;t<=T;t+=step){x.textAlign=t===0?'left':'center';x.fillText(clock(t),X(t),h-6)}
if(cursor!=null){x.strokeStyle='rgba(231,237,247,.55)';x.beginPath();x.moveTo(X(cursor),0);x.lineTo(X(cursor),h-axis);x.stroke()}
c.onpointermove=e=>{cursor=Math.max(0,Math.min(T,Math.round((e.offsetX-L)/(R-L)*T)));const s=bySec.get(cursor)||{},ms=v=>v==null?'—':v.toFixed(2)+' ms';
document.getElementById('tip').textContent=clock(cursor)+' ／ Read '+(s.readMiB||0).toFixed(2)+' MiB/s · '+(s.reads||0)+' IOPS · P95 '+ms(s.readP95Ms)+' ／ Write '+(s.writeMiB||0).toFixed(2)+' MiB/s · '+(s.writes||0)+' IOPS · P95 '+ms(s.writeP95Ms);timeline()};
c.onpointerleave=()=>{cursor=null;timeline()}}
const select=document.getElementById('files');heats.forEach((h,i)=>{let o=document.createElement('option');o.value=i;o.textContent=h.path;select.append(o)});
function heat(){const[c,x,w,h]=setup('heat');const data=heats[+select.value];if(!data){x.fillStyle='#9fadc5';x.fillText('Readイベントなし',20,40);return}let max=Math.max(1,...data.cells);for(let y=0;y<64;y++)for(let t=0;t<120;t++){let v=Math.log1p(data.cells[y*120+t])/Math.log1p(max);x.fillStyle=`rgb(${Math.round(18+v*70)},${Math.round(29+v*180)},${Math.round(47+v*145)})`;x.fillRect(t*w/120,y*h/64,Math.ceil(w/120),Math.ceil(h/64))}document.getElementById('heatTip').textContent='先頭 0 → '+(data.maxOffset/1048576).toFixed(2)+' MiB ／ 0 → '+duration.toFixed(1)+' 秒'}select.onchange=heat;window.onresize=()=>{timeline();heat()};timeline();heat();
</script></html>
""";
        File.WriteAllText(Path.Combine(session,fileName),html,new UTF8Encoding(false));
    }
}
