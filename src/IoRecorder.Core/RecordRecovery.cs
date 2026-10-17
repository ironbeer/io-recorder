using System.Diagnostics;
using System.Text.Json;

namespace IoRecorder.Core;

// What opening a record does first. A record still marked Recording whose recorder is gone was cut off (the tool or the PC
// stopped): what follows the last complete line of its files is removed (as a write-ahead log is repaired), its count and
// end time are taken from what was written, and it is marked Interrupted. Leftovers of compression that was cut off are
// tidied up in every record.
public static class RecordRecovery
{
    public const string Interrupted = "Interrupted";
    private static readonly string[] SideFiles = ["sizes.jsonl", "memory.jsonl", "processes.jsonl", "open-files.jsonl", "lucidlink.jsonl"];

    // Returns true when the record was finished as Interrupted.
    public static bool Prepare(string session)
    {
        string path = Path.Combine(session, "session.json");
        if (!File.Exists(path)) return false;
        SessionManifest m;
        try { m = JsonStore.Read<SessionManifest>(path); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException) { return false; }
        bool finished = false;
        if (m.State == "Recording")
        {
            if (IsRecording(m)) return false;
            try { Finish(session, m); finished = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; } // read-only storage: shown as it is
        }
        try
        {
            RecordLog.Tidy(session, RecordLog.Events);
            foreach (var temp in Directory.EnumerateFiles(session, "replay-*.jsonl.gz.tmp")) File.Delete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return finished;
    }

    // Every record in a folder of records, and the recordings of their replays.
    public static int PrepareAll(string root)
    {
        if (!Directory.Exists(root)) return 0;
        int finished = 0;
        foreach (var record in Directory.EnumerateDirectories(root))
        {
            if (Prepare(record)) finished++;
            string replays = Path.Combine(record, ReplayCapture.Folder);
            if (Directory.Exists(replays)) foreach (var trace in Directory.EnumerateDirectories(replays)) if (Prepare(trace)) finished++;
        }
        return finished;
    }

    // The recorder process is still the one that started it (a reused process id is not).
    private static bool IsRecording(SessionManifest m)
    {
        if (m.RecorderProcessId is not { } pid) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            return m.RecorderStartedUtc is not { } started || Math.Abs((p.StartTime.ToUniversalTime() - started.UtcDateTime).TotalSeconds) < 2;
        }
        catch (ArgumentException) { return false; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return true; } // cannot tell: left alone
    }

    private static void Finish(string session, SessionManifest m)
    {
        long removed = RecordLog.RepairTails(session, RecordLog.Events);
        foreach (var side in SideFiles) removed += RepairTail(Path.Combine(session, side));
        long count = 0, last = 0;
        foreach (var e in RecordLog.Read<IoEvent>(session, RecordLog.Events)) { count++; last = Math.Max(last, e.EndUs ?? e.StartUs); }
        m.State = Interrupted;
        m.EventsWritten = count;
        m.EndedUtc = m.StartedUtc.AddTicks(last * 10);
        m.Warnings.Add($"記録が途中で終わっていました（ツールかPCが止まった）。最後に書き出された時点（開始から約 {last / 1_000_000.0:N0} 秒）までのデータです。"
            + (m.CheckpointUtc is { } saved ? $" 欠落数などは {saved.ToLocalTime():HH:mm:ss} 時点の値です。" : " 欠落数などは記録されていません。")
            + (removed > 0 ? $" 途中で切れていた末尾 {removed:N0} バイトを取り除きました。" : ""));
        JsonStore.Write(Path.Combine(session, "session.json"), m);
    }

    // What follows the last newline of a plain file (a cut-off line, or zeros where data never reached the disk).
    public static long RepairTail(string file)
    {
        if (!File.Exists(file)) return 0;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        long end = stream.Length, keep = 0;
        var buffer = new byte[65536];
        for (long position = end; position > 0 && keep == 0;)
        {
            int n = (int)Math.Min(buffer.Length, position); position -= n;
            stream.Position = position; stream.ReadExactly(buffer, 0, n);
            for (int i = n - 1; i >= 0; i--) if (buffer[i] == (byte)'\n') { keep = position + i + 1; break; }
        }
        if (keep >= end) return 0;
        stream.SetLength(keep);
        return end - keep;
    }
}
