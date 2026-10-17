using System.Diagnostics;
using System.Runtime.InteropServices;

namespace IoRecorder.Core;

internal sealed class ProcessTracker
{
    private readonly object _gate=new();
    private readonly Dictionary<int,(string Name,int Parent)> _processes=[];
    private readonly HashSet<int> _explicit;
    private readonly HashSet<string> _names;
    private readonly bool _children;
    public ProcessTracker(RecordingOptions options)
    {
        _explicit=options.ProcessIds.ToHashSet(); _names=new(options.ProcessNames,StringComparer.OrdinalIgnoreCase); _children=options.IncludeChildren;
        var snapshot=CreateToolhelp32Snapshot(2,0);
        if(snapshot!=new nint(-1))
        {
            try
            {
                var entry=new ProcessEntry { Size=(uint)Marshal.SizeOf<ProcessEntry>() };
                if(Process32First(snapshot,ref entry)) do { Set((int)entry.Pid,Path.GetFileNameWithoutExtension(entry.Exe),(int)entry.ParentPid); } while(Process32Next(snapshot,ref entry));
            }
            finally { CloseHandle(snapshot); }
        }
    }
    public void Set(int pid,string name,int parent)
    { lock(_gate) _processes[pid]=(Path.GetFileNameWithoutExtension(name),parent); }
    public void Remove(int pid) { lock(_gate) { _processes.Remove(pid); _explicit.Remove(pid); } }
    public string Name(int pid) { lock(_gate) return _processes.TryGetValue(pid,out var p)?p.Name:pid==4?"System":"PID "+pid; }
    public bool Matches(int pid)
    {
        lock(_gate)
        {
            var seen=new HashSet<int>();
            while(pid>0 && seen.Add(pid))
            {
                if(_explicit.Contains(pid)) return true;
                if(!_processes.TryGetValue(pid,out var p)) return false;
                if(_names.Contains(p.Name)) return true;
                if(!_children) return false;
                pid=p.Parent;
            }
            return false;
        }
    }
    public int[] TargetPids() { lock(_gate) return _processes.Keys.Where(Matches).ToArray(); }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct ProcessEntry
    {
        public uint Size,Usage,Pid; public nuint DefaultHeap; public uint ModuleId,Threads,ParentPid; public int BasePriority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Exe;
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern nint CreateToolhelp32Snapshot(uint flags,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern bool Process32First(nint snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern bool Process32Next(nint snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
