using System.Numerics;

namespace IoRecorder.Core;

// How one file was read and written by the application's own requests (paging I/O excluded).
public sealed class FileAccessPattern
{
    public string Path { get; set; } = "";
    // Reads: 全体を順に / 一部を順に / 散らばる（疎） / 混在 / 数回だけ / 書き込みのみ
    public string Shape { get; set; } = "";
    // The larger of the size queried after the first access and the furthest position read or written.
    public long SizeBytes { get; set; }
    public long Reads { get; set; }
    public long ReadBytes { get; set; }
    public long UniqueReadBytes { get; set; }
    public double? ReadCoverage { get; set; }
    public double? RereadFactor { get; set; }
    // Upper bounds of power-of-two classes: 65,536 means "over 32 KiB, up to 64 KiB".
    public long ReadSizeP50Bytes { get; set; }
    public long ReadSizeP95Bytes { get; set; }
    // Reads that start where the same handle's previous read ended, and forward skips of up to 1 MiB.
    public double SequentialRatio { get; set; }
    public double NearRatio { get; set; }
    public long JumpP50Bytes { get; set; }
    // Writes: 新規に順に書く / 新規（順不同） / 末尾に追記 / 既存を順に上書き / 既存の一部を上書き / 混在 / 数回だけ
    public string WriteShape { get; set; } = "";
    public long Writes { get; set; }
    public long WriteBytes { get; set; }
    public long UniqueWriteBytes { get; set; }
    public long WriteSizeP50Bytes { get; set; }
    // Writes that continue where the same handle's previous write ended (or skip ahead by up to 1 MiB).
    public double WriteContinuousRatio { get; set; }
    // Whether the file existed when the recording started, its estimated size then, and how much of that existing data
    // was written over (deduplicated). Writes beyond it are appends or a new file's contents.
    public bool ExistedAtStart { get; set; }
    public long InitialSizeBytes { get; set; }
    public long ExistingOverwriteBytes { get; set; }
    // Per AccessPatternBuilder.ChunkSizes: bytes a cold cache of a chunked store would fetch for this file's reads.
    public long[] FetchBytes { get; set; } = [];
}

// For one split size of a chunked store (LucidLink stores files as equal-sized pieces): what the recorded reads and writes
// touch. Reads are a lower bound: empty cache, no prefetch, a cache large enough that nothing is fetched twice.
public sealed class ChunkEstimate
{
    public long ChunkBytes { get; set; }
    public long ReadChunks { get; set; }
    public long ReadFetchBytes { get; set; }
    public double? ReadAmplification { get; set; }
    // Writes merged before upload (each chunk once over the whole recording): the lower bound.
    public long WriteChunks { get; set; }
    public long WriteUploadBytes { get; set; }
    public double? WriteAmplification { get; set; }
    // Every write uploading the chunks it touches, nothing merged: the upper bound.
    public long WriteEachChunks { get; set; }
    public long WriteEachBytes { get; set; }
    // Chunks of data that existed at the start which the writes cover only in part: the rest of such a chunk is needed
    // (fetched when it is not in the cache) before the chunk can be uploaded.
    public long WritePartialChunks { get; set; }
}

public sealed class AccessPatternBuilder
{
    public static readonly long[] ChunkSizes = [64L << 10, 256L << 10, 1L << 20, 4L << 20];
    private const long NearBytes = 1L << 20;
    // Byte ranges kept for the unique (deduplicated) amounts; past this the amounts become lower bounds.
    private const long MaxRanges = 5_000_000;
    private readonly Dictionary<string,FileState> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string,FileEntry> _manifest;
    private readonly long[] _eachWriteChunks = new long[ChunkSizes.Length], _eachWriteBytes = new long[ChunkSizes.Length];
    private long _ranges;
    private bool _capped;

    // manifest: the files of the recording with what is known before the second pass (existence, size queried).
    public AccessPatternBuilder(IReadOnlyDictionary<string,FileEntry> manifest) { _manifest = manifest; }

    private sealed class FileState
    {
        public long Reads, ReadBytes, Writes, WriteBytes, Sequential, Near, WriteContinuous, MaxEnd, KnownSize;
        public readonly long[] ReadSizes = new long[64], Jumps = new long[64], WriteSizes = new long[64];
        public readonly RangeSet ReadRanges = new(), WriteRanges = new();
        // 64 KiB pieces as a sparse bitmap, one word per 4 MiB; the larger candidates are groups of these pieces.
        public readonly Dictionary<long,ulong> ReadChunks = [], WriteChunks = [];
        public readonly Dictionary<string,long> ReadEnds = [], WriteEnds = [];
    }

    // One successful application Read or Write of count bytes at e.Offset.
    public void Add(IoEvent e, long count)
    {
        if (count <= 0 || string.IsNullOrEmpty(e.Path)) return;
        if (!_files.TryGetValue(e.Path, out var f))
        {
            var entry = _manifest.GetValueOrDefault(e.Path);
            _files[e.Path] = f = new() { KnownSize = entry is { InitiallyExists: true } ? entry.ObservedSize ?? 0 : 0 };
        }
        long start = e.Offset, end = e.Offset + count;
        f.MaxEnd = Math.Max(f.MaxEnd, end);
        bool room = _ranges < MaxRanges; _capped |= !room;
        if (e.Operation == IoOperation.Write)
        {
            f.Writes++; f.WriteBytes += count; f.WriteSizes[Log2(count)]++; Mark(f.WriteChunks, start, end);
            if (room) _ranges += f.WriteRanges.Add(start, end);
            if (Continues(f.WriteEnds, e.FileObject, start)) f.WriteContinuous++;
            f.WriteEnds[e.FileObject] = end;
            // Upper bound: this write alone uploads every chunk it touches; the file's last chunk only up to the file's end.
            long fileEnd = Math.Max(f.KnownSize, f.MaxEnd);
            for (int i = 0; i < ChunkSizes.Length; i++)
            {
                long unit = ChunkSizes[i], first = start / unit, last = (end - 1) / unit;
                _eachWriteChunks[i] += last - first + 1;
                _eachWriteBytes[i] += (last - first) * unit + Math.Min(unit, fileEnd - last * unit);
            }
            return;
        }
        f.Reads++; f.ReadBytes += count; f.ReadSizes[Log2(count)]++; Mark(f.ReadChunks, start, end);
        if (room) _ranges += f.ReadRanges.Add(start, end);
        if (f.ReadEnds.TryGetValue(e.FileObject, out var previous))
        {
            long gap = start - previous;
            if (gap == 0) f.Sequential++;
            else { if (gap > 0 && gap <= NearBytes) f.Near++; f.Jumps[Log2(Math.Abs(gap))]++; }
        }
        f.ReadEnds[e.FileObject] = end;
    }

    // From where the same handle's previous request ended, or a forward skip of up to 1 MiB.
    private static bool Continues(Dictionary<string,long> ends, string handle, long start) => ends.TryGetValue(handle, out var previous) && start - previous is >= 0 and <= NearBytes;


    // manifest: the final file entries (the initial size is settled only after the second pass).
    public (List<FileAccessPattern> Files, List<ChunkEstimate> Chunks, bool Capped) Build(IReadOnlyDictionary<string,FileEntry> manifest)
    {
        var patterns = new List<FileAccessPattern>();
        var totals = ChunkSizes.Select((c, i) => new ChunkEstimate { ChunkBytes = c, WriteEachChunks = _eachWriteChunks[i], WriteEachBytes = _eachWriteBytes[i] }).ToList();
        long uniqueRead = 0, uniqueWrite = 0;
        foreach (var (path, f) in _files)
        {
            var entry = manifest.GetValueOrDefault(path);
            bool existed = entry?.InitiallyExists ?? true;
            long initial = existed ? entry?.SuggestedInitialSize ?? 0 : 0;
            long size = Math.Max(entry?.ObservedSize ?? 0, f.MaxEnd);
            long readUnique = f.ReadRanges.Total(), writeUnique = f.WriteRanges.Total();
            long overwrite = f.WriteRanges.Items.Sum(r => Math.Max(0, Math.Min(r.End, initial) - r.Start));
            double sequential = f.Reads > 0 ? (double)f.Sequential / f.Reads : 0, near = f.Reads > 0 ? (double)f.Near / f.Reads : 0;
            double writeContinuous = f.Writes > 0 ? (double)f.WriteContinuous / f.Writes : 0;
            double? coverage = size > 0 && f.Reads > 0 ? Math.Min(1, (double)readUnique / size) : null;
            var p = new FileAccessPattern
            {
                Path = path, SizeBytes = size, Reads = f.Reads, ReadBytes = f.ReadBytes, UniqueReadBytes = readUnique,
                ReadCoverage = coverage, RereadFactor = readUnique > 0 ? (double)f.ReadBytes / readUnique : null,
                ReadSizeP50Bytes = Percentile(f.ReadSizes, .5), ReadSizeP95Bytes = Percentile(f.ReadSizes, .95),
                SequentialRatio = sequential, NearRatio = near, JumpP50Bytes = Percentile(f.Jumps, .5),
                Shape = Shape(f.Reads, sequential + near, coverage),
                Writes = f.Writes, WriteBytes = f.WriteBytes, UniqueWriteBytes = writeUnique, WriteSizeP50Bytes = Percentile(f.WriteSizes, .5),
                WriteContinuousRatio = writeContinuous, ExistedAtStart = existed, InitialSizeBytes = initial, ExistingOverwriteBytes = overwrite,
                WriteShape = WriteShape(f.Writes, writeContinuous, existed && initial > 0, overwrite, writeUnique),
                FetchBytes = new long[ChunkSizes.Length]
            };
            for (int i = 0; i < ChunkSizes.Length; i++)
            {
                int shift = BitOperations.Log2((ulong)(ChunkSizes[i] >> 16));
                var (readChunks, readBytes) = Chunks(f.ReadChunks, shift, size);
                var (writeChunks, writeBytes) = Chunks(f.WriteChunks, shift, size);
                p.FetchBytes[i] = readBytes;
                totals[i].ReadChunks += readChunks; totals[i].ReadFetchBytes += readBytes;
                totals[i].WriteChunks += writeChunks; totals[i].WriteUploadBytes += writeBytes;
                totals[i].WritePartialChunks += PartialChunks(f.WriteRanges.Items, ChunkSizes[i], initial);
            }
            uniqueRead += readUnique; uniqueWrite += writeUnique;
            patterns.Add(p);
        }
        foreach (var t in totals)
        {
            t.ReadAmplification = uniqueRead > 0 ? (double)t.ReadFetchBytes / uniqueRead : null;
            t.WriteAmplification = uniqueWrite > 0 ? (double)t.WriteUploadBytes / uniqueWrite : null;
        }
        return (patterns.OrderByDescending(p => p.ReadBytes).ThenByDescending(p => p.WriteBytes).ToList(), totals, _capped);
    }

    // Mostly continuous reads (from where the previous one ended, or skipping ahead by up to 1 MiB) count as "順に".
    private static string Shape(long reads, double continuous, double? coverage)
    {
        if (reads == 0) return "書き込みのみ";
        if (reads < 4) return "数回だけ";
        if (continuous >= 0.7) return coverage >= 0.9 ? "全体を順に" : "一部を順に";
        return continuous < 0.3 ? "散らばる（疎）" : "混在";
    }

    // A file without data at the start is new; an existing file is appended to, written over, or both.
    private static string WriteShape(long writes, double continuous, bool hadData, long overwrite, long unique)
    {
        if (writes == 0) return "";
        if (writes < 4) return "数回だけ";
        if (!hadData) return continuous >= 0.7 ? "新規に順に書く" : "新規（順不同）";
        if (overwrite == 0) return "末尾に追記";
        if (overwrite >= unique * 0.9) return continuous >= 0.7 ? "既存を順に上書き" : "既存の一部を上書き";
        return "混在";
    }

    // Chunks holding data from the start (below initialSize) that the merged writes cover only in part.
    private static long PartialChunks(IReadOnlyList<(long Start, long End)> ranges, long unit, long initialSize)
    {
        long partial = 0, current = -1, covered = 0;
        void Close() { if (current >= 0 && covered < Math.Min(unit, initialSize - current * unit)) partial++; }
        foreach (var (start, end) in ranges)
            for (long position = start, stop = Math.Min(end, initialSize); position < stop;)
            {
                long chunk = position / unit, chunkEnd = Math.Min((chunk + 1) * unit, stop);
                if (chunk != current) { Close(); current = chunk; covered = 0; }
                covered += chunkEnd - position; position = chunkEnd;
            }
        Close();
        return partial;
    }

    private static void Mark(Dictionary<long,ulong> map, long start, long end)
    {
        for (long piece = start >> 16, last = (end - 1) >> 16; piece <= last; piece++)
            map[piece >> 6] = map.GetValueOrDefault(piece >> 6) | 1UL << (int)(piece & 63);
    }

    // Chunks of 64 KiB << shift touched, and their bytes (the file's last chunk counts only up to the file size).
    private static (long Chunks, long Bytes) Chunks(Dictionary<long,ulong> map, int shift, long size)
    {
        long unit = 65536L << shift, chunks = 0, bytes = 0; int width = 1 << shift;
        ulong mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        foreach (var (word, bits) in map)
            for (int group = 0; group < 64; group += width)
                if ((bits >> group & mask) != 0)
                {
                    long start = (word * 64 + group) * 65536L;
                    chunks++; bytes += size > start ? Math.Min(unit, size - start) : unit;
                }
        return (chunks, bytes);
    }

    // The smallest b with 2^b ≥ value.
    private static int Log2(long value) => value <= 1 ? 0 : 64 - BitOperations.LeadingZeroCount((ulong)(value - 1));
    private static long Percentile(long[] classes, double quantile)
    {
        long total = classes.Sum(); if (total == 0) return 0;
        long threshold = Math.Max(1, (long)Math.Ceiling(total * quantile)), seen = 0;
        for (int b = 0; b < classes.Length; b++) { seen += classes[b]; if (seen >= threshold) return 1L << b; }
        return 0;
    }
}

// Byte ranges of one file: appended as they come and merged in batches, so scattered requests cost O(n log n), not O(n²).
internal sealed class RangeSet
{
    private readonly List<(long Start, long End)> _items = [];
    private int _compactAt = 1024;

    // Returns how much the number of stored ranges changed.
    public int Add(long start, long end)
    {
        int before = _items.Count;
        _items.Add((start, end));
        if (_items.Count >= _compactAt) { Compact(); _compactAt = Math.Max(1024, _items.Count * 2); }
        return _items.Count - before;
    }

    public long Total() { Compact(); long total = 0; foreach (var (start, end) in _items) total += end - start; return total; }

    // Sorted, non-overlapping ranges.
    public IReadOnlyList<(long Start, long End)> Items { get { Compact(); return _items; } }

    private void Compact()
    {
        if (_items.Count < 2) return;
        _items.Sort((a, b) => a.Start.CompareTo(b.Start));
        int kept = 0;
        for (int i = 1; i < _items.Count; i++)
        {
            if (_items[i].Start <= _items[kept].End) _items[kept] = (_items[kept].Start, Math.Max(_items[kept].End, _items[i].End));
            else _items[++kept] = _items[i];
        }
        _items.RemoveRange(kept + 1, _items.Count - kept - 1);
    }
}
