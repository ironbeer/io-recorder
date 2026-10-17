using System.Diagnostics;
using System.Runtime.InteropServices;

namespace IoRecorder.Core;

public static class MemorySampler
{
    public static MemorySample Sample(long timeUs, IEnumerable<int>? pids = null)
    {
        var memory = new MemoryStatus { Length=(uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var perf = new PerformanceInfo { Size=(uint)Marshal.SizeOf<PerformanceInfo>() };
        GetPerformanceInfo(ref perf, perf.Size);
        var result=new MemorySample
        {
            TimeUs=timeUs,TotalBytes=memory.TotalPhysical,AvailableBytes=memory.AvailablePhysical,
            CommitBytes=(ulong)perf.CommitTotal*(ulong)perf.PageSize,
            CommitLimitBytes=(ulong)perf.CommitLimit*(ulong)perf.PageSize,
            SystemCacheBytes=(ulong)perf.SystemCache*(ulong)perf.PageSize
        };
        foreach(var pid in (pids ?? []).Distinct())
        {
            try { using var p=Process.GetProcessById(pid); result.Processes.Add(new(pid,p.ProcessName,p.WorkingSet64,p.PrivateMemorySize64)); }
            catch (Exception ex) when(ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return result;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PerformanceInfo
    {
        public uint Size;
        public nuint CommitTotal,CommitLimit,CommitPeak,PhysicalTotal,PhysicalAvailable,SystemCache,KernelTotal,KernelPaged,KernelNonpaged,PageSize;
        public uint HandleCount,ProcessCount,ThreadCount;
    }
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
    [DllImport("psapi.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(ref PerformanceInfo buffer,uint size);
}
