namespace IoRecorder.Core;

// The I/O of the processes a recording leaves out, counted without file names: per process and drive (or share), the number
// of operations and the bytes read and written. It tells afterwards whether another program worked on the same storage as
// the target (a helper that is not a child of the application, a sync client, the search indexer) without recording what it
// touched, so the next recording can add it.
public sealed class OtherProcessIo
{
    public string Process { get; set; } = "";
    // "C:", "\\server\share", a device no drive letter maps to, or "" when the path was not known.
    public string Volume { get; set; } = "";
    public long Operations { get; set; }
    public long ReadBytes { get; set; }
    public long WriteBytes { get; set; }

    // The left-out processes that used the drives the record's own I/O used, the busiest first ("SearchIndexer（Z:）1.2 GiB・…").
    public static string? Summary(SessionManifest manifest,AnalysisResult analysis,int count=3)
    {
        var volumes=analysis.Files.Select(f=>PathPolicy.VolumeOf(f.Path)).Where(v=>v.Length>0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows=manifest.OtherProcessIo.Where(r=>volumes.Contains(r.Volume) && (r.ReadBytes+r.WriteBytes>=1L<<20 || r.Operations>=1000))
            .OrderByDescending(r=>r.ReadBytes+r.WriteBytes).ThenByDescending(r=>r.Operations).Take(count).ToList();
        if(rows.Count==0) return null;
        static string Size(long bytes)=>bytes>=1L<<30?$"{bytes/1073741824.0:N1} GiB":$"{bytes/1048576.0:N0} MiB";
        return string.Join("・",rows.Select(r=>$"{r.Process}（{r.Volume}）{Size(r.ReadBytes+r.WriteBytes)}・{r.Operations:N0} 件"));
    }
}

// Counts the left-out I/O during a recording (on the thread that reads the events; read the result after it has stopped).
public sealed class OtherProcessCounter
{
    private const int Limit=4096;
    private readonly Dictionary<(string Process,string Volume),OtherProcessIo> _rows=[];
    public long Events { get; private set; }
    public void Add(string process,string path,IoOperation operation,long bytes)
    {
        Events++;
        (string Process,string Volume) key=(process,PathPolicy.VolumeOf(path));
        if(!_rows.TryGetValue(key,out var row))
        {
            if(_rows.Count>=Limit) return;
            _rows[key]=row=new() { Process=key.Process,Volume=key.Volume };
        }
        row.Operations++;
        if(operation==IoOperation.Read) row.ReadBytes+=Math.Max(0,bytes); else if(operation==IoOperation.Write) row.WriteBytes+=Math.Max(0,bytes);
    }
    public List<OtherProcessIo> Top(int count=200)=>_rows.Values.OrderByDescending(r=>r.ReadBytes+r.WriteBytes).ThenByDescending(r=>r.Operations).Take(count).ToList();
}
