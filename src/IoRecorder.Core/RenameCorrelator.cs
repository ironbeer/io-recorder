namespace IoRecorder.Core;

// FileIo Rename carries only the source name. The FCB (FileKey) survives a rename, so the destination is the first later
// name seen for the same FileKey. Renames are held until resolved or timed out; an event is never changed after publish.
public sealed class RenameCorrelator(Action<IoEvent> publish,long timeoutUs=2_000_000,int capacity=4096)
{
    private sealed class Pending(IoEvent e,ulong key) { public IoEvent Event=e; public ulong Key=key; public bool Completed; }
    private readonly Dictionary<ulong,Pending> _byKey=[];
    private readonly Dictionary<IoEvent,Pending> _byEvent=new(ReferenceEqualityComparer.Instance);
    private readonly Queue<(long DeadlineUs,Pending Item)> _deadlines=new();
    public int Count=>_byEvent.Count;

    public void Started(ulong fileKey,IoEvent rename)
    {
        if(fileKey==0) return;
        if(_byKey.Remove(fileKey,out var older)) older.Key=0;
        var item=new Pending(rename,fileKey); _byKey[fileKey]=item; _byEvent[rename]=item;
    }
    public void Completed(IoEvent rename,long nowUs)
    {
        if(!_byEvent.TryGetValue(rename,out var item)) { publish(rename); return; }
        if(rename.Failed || rename.DestinationPath is not null) { Release(item,null); return; }
        item.Completed=true; _deadlines.Enqueue((nowUs+timeoutUs,item));
        if(_byEvent.Count>capacity) Expire(long.MaxValue);
    }
    public void Observe(ulong fileKey,string path,long nowUs)
    {
        if(_byKey.Count>0 && fileKey!=0 && path.Length>0 && _byKey.TryGetValue(fileKey,out var item) && !path.Equals(item.Event.Path,StringComparison.OrdinalIgnoreCase))
        {
            item.Event.DestinationPath=path; item.Event.Note="Rename destination correlated from a later name on the same FileKey";
            _byKey.Remove(fileKey); item.Key=0;
            if(item.Completed) Release(item,null);
        }
        if(_deadlines.Count>0) Expire(nowUs);
    }
    // The file left memory (FileDelete): its key may now name another file. A share's file (SMB) leaves soon after it is
    // closed and its key is reused within moments, which once named an unrelated file as a rename's destination; the rename
    // keeps waiting for nothing more and is published without a destination (the replay skips it).
    public void Gone(ulong fileKey)
    {
        if(fileKey==0 || !_byKey.Remove(fileKey,out var item)) return;
        item.Key=0;
        if(item.Completed) Release(item,"Rename destination not observed before the file left memory; replay will skip");
    }
    // A request superseded before completion (same IRP reported again) must not be published separately.
    public void Forget(IoEvent rename) { if(_byEvent.Remove(rename,out var item) && item.Key!=0) _byKey.Remove(item.Key); }
    public void Flush() { foreach(var item in _byEvent.Values.ToArray()) Release(item,"Rename destination not observed before stop; replay will skip"); _deadlines.Clear(); }
    private void Expire(long nowUs)
    {
        while(_deadlines.TryPeek(out var next) && next.DeadlineUs<=nowUs)
        {
            _deadlines.Dequeue();
            if(_byEvent.ContainsKey(next.Item.Event)) Release(next.Item,"Rename destination not observed; replay will skip");
        }
    }
    private void Release(Pending item,string? note)
    {
        if(!_byEvent.Remove(item.Event)) return;
        if(item.Key!=0 && _byKey.TryGetValue(item.Key,out var current) && ReferenceEquals(current,item)) _byKey.Remove(item.Key);
        if(note is not null && item.Event.DestinationPath is null) item.Event.Note=note;
        publish(item.Event);
    }
}
