namespace IoRecorder.Core;

// Names whose last open found nothing there (not found), until an open or create succeeds on them. A request on an open file
// cannot be on such a name: the name came with a key or file object Windows had already given to another file (seen on a
// share, where they are reused within moments), so the recorder leaves that request unnamed instead.
internal sealed class MissingNames(int limit=200_000)
{
    private readonly HashSet<string> _names=new(StringComparer.OrdinalIgnoreCase);
    public void Opened(string path,uint? status)
    {
        if(path.Length==0) return;
        // STATUS_OBJECT_NAME_NOT_FOUND / STATUS_OBJECT_PATH_NOT_FOUND
        if(status is 0xC0000034 or 0xC000003A) { if(_names.Count<limit) _names.Add(path); }
        else if(status is { } s && unchecked((int)s)>=0) _names.Remove(path);
    }
    public bool Contains(string path)=>path.Length>0 && _names.Count>0 && _names.Contains(path);
}
