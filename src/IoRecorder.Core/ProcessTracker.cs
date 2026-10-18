using System.Runtime.InteropServices;

namespace IoRecorder.Core;

// Which processes are recorded. A process is decided once, when it is first seen: a target by PID or name, by its program
// being in one of the program folders (the other programs installed with the chosen application), or as a child of a target
// (IncludeChildren). The decision is kept for its lifetime, so a child stays recorded after its parent ends (a render process
// after the application closes, the main program after a launcher that started it exits), and a later process that happens
// to reuse an ended parent's PID never makes its own children targets.
internal sealed class ProcessTracker
{
    private sealed class Entry(string name,int parent,bool target)
    { public string Name=name; public int Parent=parent; public bool Target=target; public bool Ended; }
    private readonly object _gate=new();
    private readonly Dictionary<int,Entry> _processes=[];
    private readonly HashSet<int> _explicit;
    private readonly HashSet<string> _names;
    private readonly string[] _folders;
    private readonly bool _children;
    public ProcessTracker(RecordingOptions options) : this(options,Snapshot()) { }
    // Processes already running: (pid, name, parent pid, start time, program file; the last two null when they cannot be read).
    internal ProcessTracker(RecordingOptions options,IEnumerable<(int Pid,string Name,int Parent,long? Started,string? Program)> running)
    {
        _explicit=options.ProcessIds.ToHashSet(); _names=new(options.ProcessNames.Select(Path.GetFileNameWithoutExtension).OfType<string>(),StringComparer.OrdinalIgnoreCase); _children=options.IncludeChildren;
        _folders=options.ProgramFolders.Select(f=>Path.TrimEndingDirectorySeparator(f)+"\\").ToArray();
        var list=running.GroupBy(p=>p.Pid).ToDictionary(g=>g.Key,g=>g.First());
        var decided=new Dictionary<int,bool>();
        bool Decide(int pid,int depth)
        {
            if(decided.TryGetValue(pid,out var known)) return known;
            var p=list[pid];
            bool target=Own(pid,p.Name,p.Program);
            // The parent PID of a running process may by now belong to another process that started later: only a parent that
            // started before its child is its parent.
            if(!target && _children && depth<256 && p.Parent!=pid && list.TryGetValue(p.Parent,out var parent) && !(parent.Started>p.Started))
                target=Decide(p.Parent,depth+1);
            return decided[pid]=target;
        }
        foreach(var p in list.Values) _processes[p.Pid]=new(Path.GetFileNameWithoutExtension(p.Name),p.Parent,Decide(p.Pid,0));
    }
    // Whether the program file of a starting process is needed to decide it (it is read only then).
    public bool FollowsFolders=>_folders.Length>0;
    // A target in its own right, without its parent.
    private bool Own(int pid,string name,string? program)=>
        _explicit.Contains(pid) || _names.Contains(Path.GetFileNameWithoutExtension(name)) || program is not null && _folders.Any(f=>program.StartsWith(f,StringComparison.OrdinalIgnoreCase));
    // A process that starts now: its parent is alive, so the parent's decision carries over.
    public void Started(int pid,string name,int parent,string? program=null)
    {
        name=Path.GetFileNameWithoutExtension(name);
        lock(_gate)
            _processes[pid]=new(name,parent,Own(pid,name,program) || _children && parent!=pid && _processes.TryGetValue(parent,out var p) && p.Target);
    }
    // A process reported as running when the session starts: known from the snapshot unless it started in between.
    public void Running(int pid,string name,int parent,string? program=null)
    {
        lock(_gate) if(_processes.TryGetValue(pid,out var e) && e.Name.Equals(Path.GetFileNameWithoutExtension(name),StringComparison.OrdinalIgnoreCase)) return;
        Started(pid,name,parent,program);
    }
    // A process that ended keeps its name and decision: the closing of its files and the unmapping of its memory are reported
    // after its end. Its PID is decided again only when a new process starts with it (Started).
    public void Ended(int pid) { lock(_gate) { if(_processes.TryGetValue(pid,out var p)) p.Ended=true; _explicit.Remove(pid); } }
    public string Name(int pid) { lock(_gate) return _processes.TryGetValue(pid,out var p)?p.Name:pid==4?"System":"PID "+pid; }
    public bool Matches(int pid) { lock(_gate) return _processes.TryGetValue(pid,out var p)?p.Target:_explicit.Contains(pid); }
    // The targets still running.
    public int[] TargetPids() { lock(_gate) return _processes.Where(p=>p.Value.Target && !p.Value.Ended).Select(p=>p.Key).ToArray(); }

    private static List<(int,string,int,long?,string?)> Snapshot()
    {
        var list=new List<(int,string,int,long?,string?)>();
        var snapshot=CreateToolhelp32Snapshot(2,0);
        if(snapshot==new nint(-1)) return list;
        try
        {
            var entry=new ProcessEntry { Size=(uint)Marshal.SizeOf<ProcessEntry>() };
            if(Process32First(snapshot,ref entry)) do { list.Add(((int)entry.Pid,entry.Exe,(int)entry.ParentPid,StartTime(entry.Pid),AppLauncher.ProgramOf((int)entry.Pid))); } while(Process32Next(snapshot,ref entry));
        }
        finally { CloseHandle(snapshot); }
        return list;
    }
    private static long? StartTime(uint pid)
    {
        var process=OpenProcess(0x1000,false,pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if(process==0) return null;
        try { return GetProcessTimes(process,out long created,out _,out _,out _)?created:null; }
        finally { CloseHandle(process); }
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct ProcessEntry
    {
        public uint Size,Usage,Pid; public nuint DefaultHeap; public uint ModuleId,Threads,ParentPid; public int BasePriority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Exe;
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern nint CreateToolhelp32Snapshot(uint flags,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern bool Process32First(nint snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern bool Process32Next(nint snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(nint process,out long created,out long exited,out long kernel,out long user);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
