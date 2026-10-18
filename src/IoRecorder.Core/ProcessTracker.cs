using System.Runtime.InteropServices;

namespace IoRecorder.Core;

// Which processes are recorded. A process is decided once, when it is first seen: a target by PID or name, or a child of a
// target (IncludeChildren). The decision is kept for its lifetime, so a child stays recorded after its parent ends (a render
// process after the application closes, the main program after a launcher that started it exits), and a later process that
// happens to reuse an ended parent's PID never makes its own children targets.
internal sealed class ProcessTracker
{
    private sealed class Entry(string name,int parent,bool target)
    { public string Name=name; public int Parent=parent; public bool Target=target; }
    private readonly object _gate=new();
    private readonly Dictionary<int,Entry> _processes=[];
    private readonly HashSet<int> _explicit;
    private readonly HashSet<string> _names;
    private readonly bool _children;
    public ProcessTracker(RecordingOptions options) : this(options,Snapshot()) { }
    // Processes already running: (pid, name, parent pid, start time or null when it cannot be read).
    internal ProcessTracker(RecordingOptions options,IEnumerable<(int Pid,string Name,int Parent,long? Started)> running)
    {
        _explicit=options.ProcessIds.ToHashSet(); _names=new(options.ProcessNames.Select(Path.GetFileNameWithoutExtension).OfType<string>(),StringComparer.OrdinalIgnoreCase); _children=options.IncludeChildren;
        var list=running.GroupBy(p=>p.Pid).ToDictionary(g=>g.Key,g=>g.First());
        var decided=new Dictionary<int,bool>();
        bool Decide(int pid,int depth)
        {
            if(decided.TryGetValue(pid,out var known)) return known;
            var p=list[pid];
            bool target=_explicit.Contains(pid) || _names.Contains(Path.GetFileNameWithoutExtension(p.Name));
            // The parent PID of a running process may by now belong to another process that started later: only a parent that
            // started before its child is its parent.
            if(!target && _children && depth<256 && p.Parent!=pid && list.TryGetValue(p.Parent,out var parent) && !(parent.Started>p.Started))
                target=Decide(p.Parent,depth+1);
            return decided[pid]=target;
        }
        foreach(var p in list.Values) _processes[p.Pid]=new(Path.GetFileNameWithoutExtension(p.Name),p.Parent,Decide(p.Pid,0));
    }
    // A process that starts now: its parent is alive, so the parent's decision carries over.
    public void Started(int pid,string name,int parent)
    {
        name=Path.GetFileNameWithoutExtension(name);
        lock(_gate)
            _processes[pid]=new(name,parent,_explicit.Contains(pid) || _names.Contains(name) || _children && parent!=pid && _processes.TryGetValue(parent,out var p) && p.Target);
    }
    // A process reported as running when the session starts: known from the snapshot unless it started in between.
    public void Running(int pid,string name,int parent)
    {
        lock(_gate) if(_processes.TryGetValue(pid,out var e) && e.Name.Equals(Path.GetFileNameWithoutExtension(name),StringComparison.OrdinalIgnoreCase)) return;
        Started(pid,name,parent);
    }
    public void Remove(int pid) { lock(_gate) { _processes.Remove(pid); _explicit.Remove(pid); } }
    public string Name(int pid) { lock(_gate) return _processes.TryGetValue(pid,out var p)?p.Name:pid==4?"System":"PID "+pid; }
    public bool Matches(int pid) { lock(_gate) return _processes.TryGetValue(pid,out var p)?p.Target:_explicit.Contains(pid); }
    public int[] TargetPids() { lock(_gate) return _processes.Where(p=>p.Value.Target).Select(p=>p.Key).ToArray(); }

    private static List<(int,string,int,long?)> Snapshot()
    {
        var list=new List<(int,string,int,long?)>();
        var snapshot=CreateToolhelp32Snapshot(2,0);
        if(snapshot==new nint(-1)) return list;
        try
        {
            var entry=new ProcessEntry { Size=(uint)Marshal.SizeOf<ProcessEntry>() };
            if(Process32First(snapshot,ref entry)) do { list.Add(((int)entry.Pid,entry.Exe,(int)entry.ParentPid,StartTime(entry.Pid))); } while(Process32Next(snapshot,ref entry));
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
