using System.IO.Compression;

namespace IoRecorder.Core;

// A record as one zip, to send it (「記録をzipで書き出す」) and to open it elsewhere (「zipから取り込む」). The zip holds the record's
// folder with what was measured; the summaries made from it (analysis, reports, the event CSV) are left out and made again when
// the record is opened.
public static class RecordArchive
{
    private static readonly HashSet<string> Derived = new(StringComparer.OrdinalIgnoreCase)
    { "analysis.json", NarrowedAnalysis.FileName, "file-manifest.json", "timeline.csv", "access-patterns.csv", "report.html", NarrowedAnalysis.ReportName, "events.csv" };
    private const long LargeFile = 64L << 20;

    // Writes <folder>\<record folder name>.zip (another name when it exists) and returns its path. The zip is written under a
    // temporary name and renamed when complete, so a cut-off export never leaves a zip that looks finished.
    public static async Task<string> ExportAsync(string session, string folder, IProgress<WorkProgress>? work = null, CancellationToken token = default)
    {
        session = Path.TrimEndingDirectorySeparator(Path.GetFullPath(session));
        if (!File.Exists(Path.Combine(session, "session.json"))) throw new FileNotFoundException("記録のフォルダではありません（session.json がありません）: " + session);
        // A record cut off by a crash is finished first; one still being recorded cannot be sent yet.
        RecordRecovery.Prepare(session);
        if (JsonStore.Read<SessionManifest>(Path.Combine(session, "session.json")).State == "Recording")
            throw new InvalidOperationException("この記録はまだ記録中です。停止して保存してから書き出してください。");
        // Parts of the event log closed shortly before are compressed in the background; the zip takes the finished ones.
        work?.Report(new("zipに書き出す準備をしています", 0, 0, "記録の圧縮が終わるのを待っています"));
        await LogCompressor.WhenIdleAsync(token);
        string name = Path.GetFileName(session);
        var files = Directory.EnumerateFiles(session, "*", SearchOption.AllDirectories)
            .Where(f => !Derived.Contains(Path.GetFileName(f)) && !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f)).OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).ToList();
        long total = files.Sum(f => f.Length), done = 0;
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, name + ".zip");
        for (int n = 2; File.Exists(target); n++) target = Path.Combine(folder, $"{name} ({n}).zip");
        string temp = target + ".part";
        var buffer = new byte[1 << 20];
        try
        {
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    // Compressed parts of the event log are stored as they are; the rest is compressed (quickly when large).
                    var level = file.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? CompressionLevel.NoCompression : file.Length > LargeFile ? CompressionLevel.Fastest : CompressionLevel.Optimal;
                    var entry = zip.CreateEntry(name + "/" + Path.GetRelativePath(session, file.FullName).Replace('\\', '/'), level);
                    entry.LastWriteTime = file.LastWriteTime;
                    await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
                    await using var output = entry.Open();
                    int read;
                    while ((read = await input.ReadAsync(buffer, token)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                        done += read;
                        work?.Report(new("zipに書き出しています", done, Math.Max(1, total), $"{done / 1048576.0:N0} / {total / 1048576.0:N0} MiB · {file.Name}"));
                    }
                }
            }
            File.Move(temp, target);
        }
        catch
        {
            try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        work?.Report(new("zipに書き出しました", 1, 1, target));
        return target;
    }

    // Unpacks a record's zip into <recordings>\<its folder name> (another name when it exists) and returns that folder. The record
    // is the folder of the zip that holds session.json (at the top, or in one folder). Nothing is written outside that folder.
    public static string Import(string zipPath, string recordings, IProgress<WorkProgress>? work = null, CancellationToken token = default)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var marker = zip.Entries.Where(e => e.Name.Equals("session.json", StringComparison.OrdinalIgnoreCase) && e.FullName.Count(c => c == '/' || c == '\\') <= 1)
            .OrderBy(e => e.FullName.Length).FirstOrDefault() ?? throw new InvalidDataException("このzipには記録（session.json）がありません。「記録をzipで書き出す」で作ったzipを選んでください。");
        string prefix = marker.FullName[..^marker.Name.Length];
        string name = prefix.Length > 0 ? prefix.TrimEnd('/', '\\') : Path.GetFileNameWithoutExtension(zipPath);
        name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (name.Length == 0) name = "imported";
        var entries = zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length > 0).ToList();
        long total = entries.Sum(e => e.Length), done = 0;
        Directory.CreateDirectory(recordings);
        if (Path.GetPathRoot(Path.GetFullPath(recordings)) is { Length: 3 } drive && new DriveInfo(drive).AvailableFreeSpace < total)
            throw new IOException($"展開に必要な容量 {total / 1073741824.0:N1} GiB が、保存先の空き容量を超えています。");
        string destination = Path.Combine(recordings, name);
        for (int n = 2; Directory.Exists(destination) || File.Exists(destination); n++) destination = Path.Combine(recordings, $"{name} ({n})");
        string temp = destination + ".importing";
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        try
        {
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                string path = Path.GetFullPath(Path.Combine(temp, entry.FullName[prefix.Length..].Replace('/', '\\')));
                if (!PathPolicy.IsUnder(path, temp) || path.Equals(Path.TrimEndingDirectorySeparator(temp), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("zipの中に、展開先の外を指すファイルがあります: " + entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: false);
                done += entry.Length;
                work?.Report(new("zipから取り込んでいます", done, Math.Max(1, total), $"{done / 1048576.0:N0} / {total / 1048576.0:N0} MiB · {entry.Name}"));
            }
            Directory.Move(temp, destination);
        }
        catch
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        return destination;
    }
}
