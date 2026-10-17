using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace IoRecorder.Core;

// The event log of a record on disk, kept the way log rotation does: JSON lines are appended to numbered segments
// (events-000001.jsonl, ...); a segment is closed at a size or age limit and compressed afterwards in the background
// (LogCompressor). The segment being written is never compressed, so a crash leaves at most a cut-off last line, and a
// closed segment is replaced by its .gz only once the .gz is complete. Records made before have one events.jsonl.
public static class RecordLog
{
    public const string Events = "events";
    public const long SegmentBytes = 128L << 20;
    public static readonly TimeSpan SegmentAge = TimeSpan.FromMinutes(10);

    // The parts of a log in reading order: the single file of an older record, then each segment (its .gz once compressed).
    public static List<string> Files(string folder, string name)
    {
        var files = new List<string>();
        string single = Path.Combine(folder, name + ".jsonl");
        if (File.Exists(single)) files.Add(single); else if (File.Exists(single + ".gz")) files.Add(single + ".gz");
        if (!Directory.Exists(folder)) return files;
        var segments = new SortedDictionary<int, string>();
        var pattern = new Regex("^" + Regex.Escape(name) + @"-(\d+)\.jsonl(\.gz)?$", RegexOptions.IgnoreCase);
        foreach (var file in Directory.EnumerateFiles(folder, name + "-*.jsonl*"))
            if (pattern.Match(Path.GetFileName(file)) is { Success: true } m)
            {
                int number = int.Parse(m.Groups[1].Value);
                if (m.Groups[2].Success) segments[number] = file; else segments.TryAdd(number, file);
            }
        files.AddRange(segments.Values);
        return files;
    }
    public static bool Exists(string folder, string name) => Files(folder, name).Count > 0;
    public static IEnumerable<T> Read<T>(string folder, string name)
    {
        foreach (var file in Files(folder, name)) foreach (var item in JsonStore.Lines<T>(file)) yield return item;
    }
    // When the log last changed (a compressed segment keeps the time of the segment it replaced).
    public static DateTime LastWriteUtc(string folder, string name) =>
        Files(folder, name).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
    // The last count items, reading only the last parts (a plain part from its end).
    public static List<T> Tail<T>(string folder, string name, int count)
    {
        var parts = new List<List<T>>(); int total = 0;
        foreach (var file in Enumerable.Reverse(Files(folder, name)))
        {
            var items = file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? JsonStore.Lines<T>(file).ToList() : TailPlain<T>(file, count - total);
            parts.Insert(0, items); total += items.Count;
            if (total >= count) break;
        }
        return parts.SelectMany(p => p).TakeLast(count).ToList();
    }
    private static List<T> TailPlain<T>(string file, int count)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, stream.Length - (long)count * 1024);
        stream.Position = start;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        if (start > 0) reader.ReadLine(); // the first line may be cut in the middle
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) if (line.Trim('\0').Length > 0) lines.Add(line);
        var items = new List<T>();
        foreach (var line in lines.TakeLast(count))
            try { if (System.Text.Json.JsonSerializer.Deserialize<T>(line, JsonStore.Options) is { } item) items.Add(item); }
            catch (System.Text.Json.JsonException) { } // a last line still being written
        return items;
    }

    // After a crash: removes what follows the last complete line of each plain part (a cut-off line, or zeros where the
    // file had grown but its data never reached the disk). Returns the bytes removed.
    public static long RepairTails(string folder, string name) =>
        Files(folder, name).Where(f => !f.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)).Sum(RecordRecovery.RepairTail);
    // Leftovers of compression that was cut off: a .gz.tmp is removed; a plain part whose .gz is complete is removed; any
    // other plain part of a log that is no longer written is compressed.
    public static void Tidy(string folder, string name, bool compress = true)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var temp in Directory.EnumerateFiles(folder, name + "*.jsonl.gz.tmp")) TryDelete(temp);
        foreach (var file in Files(folder, name))
        {
            if (file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            { string plain = file[..^3]; if (File.Exists(plain)) TryDelete(plain); }
            else if (compress && !Path.GetFileName(file).Equals(name + ".jsonl", StringComparison.OrdinalIgnoreCase)) LogCompressor.Enqueue(file);
        }
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
}

// Appends lines to the numbered segments of a log; a new segment starts at RecordLog.SegmentBytes or SegmentAge, and a
// closed one is handed to LogCompressor. Flush hands the written lines to the OS (they survive the tool ending); with
// toDisk the OS also writes them to the disk (they survive a power loss).
public sealed class RecordLogWriter : IDisposable
{
    private readonly string _folder, _name;
    private readonly long _maxBytes;
    private readonly TimeSpan _maxAge;
    private readonly bool _compress;
    private readonly object _gate = new();
    private FileStream? _file;
    private StreamWriter? _writer;
    private int _number;
    private long _bytes;
    private DateTime _opened;
    private bool _dirty;
    public RecordLogWriter(string folder, string name, long? maxBytes = null, TimeSpan? maxAge = null, bool compress = true)
    {
        _folder = folder; _name = name; _maxBytes = maxBytes ?? RecordLog.SegmentBytes; _maxAge = maxAge ?? RecordLog.SegmentAge; _compress = compress;
        lock (_gate) Rotate();
    }
    public int Segments { get { lock (_gate) return _number; } }
    public void Write(string line)
    {
        lock (_gate)
        {
            if (_writer is null) throw new ObjectDisposedException(nameof(RecordLogWriter));
            if (_bytes >= _maxBytes || DateTime.UtcNow - _opened >= _maxAge) Rotate();
            _writer!.Write(line); _writer.Write('\n');
            _bytes += line.Length + 1; _dirty = true;  // characters, close enough to bytes for a size limit
        }
    }
    public void Flush(bool toDisk = false)
    {
        lock (_gate)
        {
            if (_writer is null) return;
            if (_dirty) { _writer.Flush(); _dirty = false; }
            if (toDisk) _file!.Flush(true);
        }
    }
    private void Rotate()
    {
        Seal();
        _number++;
        string path = Path.Combine(_folder, $"{_name}-{_number:D6}.jsonl");
        _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, 1 << 20);
        _writer = new StreamWriter(_file, new UTF8Encoding(false), 1 << 16);
        _bytes = 0; _opened = DateTime.UtcNow; _dirty = false;
    }
    private void Seal()
    {
        if (_writer is null) return;
        _writer.Flush(); _file!.Flush(true);
        string path = _file.Name;
        _writer.Dispose(); _writer = null; _file = null;
        if (_compress) LogCompressor.Enqueue(path);
    }
    public void Dispose() { lock (_gate) Seal(); }
}

// Compresses closed log files one at a time, on a thread with background CPU and I/O priority so that it does not compete
// with the work being recorded: <file>.gz.tmp is written and flushed to the disk, renamed to <file>.gz, then the original
// is deleted. Whenever this is cut off, one complete copy remains (RecordLog.Tidy finishes the rest later).
public static class LogCompressor
{
    private static readonly BlockingCollection<string> Queue = [];
    private static readonly ConcurrentDictionary<string, byte> Queued = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static Thread? _thread;
    private static int _pending;
    public static int Pending => Volatile.Read(ref _pending);
    public static void Enqueue(string path)
    {
        if (!Queued.TryAdd(path, 0)) return;
        Interlocked.Increment(ref _pending); Queue.Add(path);
        lock (Gate)
        {
            if (_thread is not null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "IO Recorder log compression" };
            _thread.Start();
        }
    }
    public static async Task WhenIdleAsync(CancellationToken token = default) { while (Pending > 0) await Task.Delay(50, token); }
    private static void Run()
    {
        SetThreadPriority(GetCurrentThread(), 0x00010000); // THREAD_MODE_BACKGROUND_BEGIN: low CPU, I/O and memory priority
        foreach (var path in Queue.GetConsumingEnumerable())
        {
            try { Compress(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { } // left plain; RecordLog.Tidy tries again
            finally { Queued.TryRemove(path, out _); Interlocked.Decrement(ref _pending); }
        }
    }
    public static void Compress(string path)
    {
        if (!File.Exists(path)) return;
        string gz = path + ".gz", temp = gz + ".tmp";
        var written = File.GetLastWriteTimeUtc(path);
        using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 20, FileOptions.SequentialScan))
        using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            using (var zip = new GZipStream(target, CompressionLevel.Fastest, leaveOpen: true)) source.CopyTo(zip);
            target.Flush(true);
        }
        // The record's analysis is up to date with the plain file; the copy keeps its time so it is not analyzed again.
        File.SetLastWriteTimeUtc(temp, written);
        File.Move(temp, gz, true);
        File.Delete(path);
    }
    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool SetThreadPriority(nint thread, int priority);
}
