namespace IoRecorder.Core;

// Before a file is created on a share, Windows checks the folder it goes into, in the application's thread: it opens the
// folder by its name with a trailing backslash (renders\), asks about it, closes it, and the creation follows within a few µs
// (seen before every new file on a Samba share, 0.7 ms each). Recorded as the application's requests, it was sent again by the
// replay, which opens a folder for listing and waited 13 ms on average for it while other threads created files there; the
// test machine's Windows makes its own check for each replayed creation anyway. Only this exact shape is taken: a folder
// handle used for queries only, then the creation of a file in that folder by the same thread right after it closed.
public sealed class ParentFolderChecks
{
    public const long GapUs = 1000;
    private sealed class Check(IoEvent open) { public string FileObject = open.FileObject ?? ""; public string Folder = open.Path.TrimEnd('\\'); public List<long> Sequences = [open.Sequence]; public long? ClosedUs; }
    private readonly Dictionary<(int,int),Check> _open = [];
    private readonly HashSet<long> _checks = [];
    public int Count => _checks.Count;
    public bool Contains(IoEvent e) => _checks.Contains(e.Sequence);

    // For every event of the record, in the order each thread issued them.
    public void Observe(IoEvent e)
    {
        var thread = (e.ProcessId, e.ThreadId);
        if (_open.Remove(thread, out var check))
        {
            if (check.ClosedUs is { } closed)
            {
                if (e.Operation == IoOperation.Open && !e.Failed && e.CreateDisposition is 0 or 2 or 3 or 5 && e.StartUs - closed <= GapUs
                    && string.Equals(Path.GetDirectoryName(e.Path), check.Folder, StringComparison.OrdinalIgnoreCase))
                    foreach (long sequence in check.Sequences) _checks.Add(sequence);
            }
            else if (e.FileObject == check.FileObject && e.Operation is IoOperation.QueryInfo or IoOperation.Cleanup or IoOperation.Close)
            {
                check.Sequences.Add(e.Sequence);
                if (e.Operation == IoOperation.Close) check.ClosedUs = e.EndUs ?? e.StartUs;
                _open[thread] = check;
                return;
            }
        }
        if (e.Operation == IoOperation.Open && !e.Failed && e.Path.Length > 3 && e.Path.EndsWith('\\') && !string.IsNullOrEmpty(e.FileObject))
            _open[thread] = new Check(e);
    }
}
