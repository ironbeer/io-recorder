using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json.Serialization;

namespace IoRecorder.Core;

// An application the recorder starts once the recording runs (RecordingOptions.Launch), so its start-up is recorded too
// (opening the last project, listing its footage).
public sealed class AppLaunch
{
    // As the Start menu shows it.
    public string Name { get; set; } = "";
    // The program file.
    public string Path { get; set; } = "";
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    // The name the recording follows (with its child processes).
    [JsonIgnore] public string ProcessName => System.IO.Path.GetFileNameWithoutExtension(Path);
}

// A program running now (AppLauncher.Running): the application it is, one of its processes (the one with a window, if any),
// how many processes run it and whether one has a window.
public sealed record RunningProgram(AppLaunch App,int Pid,int Count,bool Window,string? Title);

public static class AppLauncher
{
    // The applications of the Start menu (for all users and for this user): shortcuts to a program file that exists, by name.
    public static List<AppLaunch> StartMenu()
    {
        var apps=new Dictionary<string,AppLaunch>(StringComparer.OrdinalIgnoreCase);
        foreach(var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
        {
            if(string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            foreach(var link in Directory.EnumerateFiles(folder,"*.lnk",new EnumerationOptions { RecurseSubdirectories=true,IgnoreInaccessible=true }))
            {
                string name=System.IO.Path.GetFileNameWithoutExtension(link);
                if(name.Contains("uninstall",StringComparison.OrdinalIgnoreCase) || name.Contains("アンインストール",StringComparison.Ordinal)) continue;
                if(Resolve(link) is not { } app || !app.Path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase) || !File.Exists(app.Path)) continue;
                // One entry per program; of several shortcuts to it, the shortest name.
                if(!apps.TryGetValue(app.Path,out var known) || app.Name.Length<known.Name.Length) apps[app.Path]=app;
            }
        }
        return apps.Values.OrderBy(a=>a.Name,StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // A program file chosen by hand, or a shortcut to one.
    public static AppLaunch? FromFile(string path)
    {
        if(path.EndsWith(".lnk",StringComparison.OrdinalIgnoreCase)) return Resolve(path);
        if(!File.Exists(path)) return null;
        var description=FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
        return new() { Name=string.IsNullOrEmpty(description)?System.IO.Path.GetFileNameWithoutExtension(path):description,Path=System.IO.Path.GetFullPath(path) };
    }

    public static AppLaunch? Resolve(string shortcut)
    {
        object? instance=null;
        try
        {
            instance=new ShellLink();
            ((IPersistFile)instance).Load(shortcut,0);
            var link=(IShellLinkW)instance; var text=new StringBuilder(1024);
            link.GetPath(text,text.Capacity,0,0);
            string target=Environment.ExpandEnvironmentVariables(text.ToString());
            if(target.Length==0) return null;
            text.Clear(); link.GetArguments(text,text.Capacity); string arguments=text.ToString();
            text.Clear(); link.GetWorkingDirectory(text,text.Capacity); string directory=Environment.ExpandEnvironmentVariables(text.ToString());
            return new() { Name=System.IO.Path.GetFileNameWithoutExtension(shortcut),Path=target,Arguments=arguments.Length>0?arguments:null,WorkingDirectory=directory.Length>0?directory:null };
        }
        catch(Exception ex) when(ex is COMException or UnauthorizedAccessException or FileNotFoundException or ArgumentException or InvalidCastException) { return null; }
        finally { if(instance is not null) Marshal.ReleaseComObject(instance); }
    }

    // The programs running now, one entry per program file: the ones with a window first. Programs whose file cannot be read
    // (protected system processes) are left out; they cannot be chosen as an application.
    public static List<RunningProgram> Running()
    {
        var found=new Dictionary<string,RunningProgram>(StringComparer.OrdinalIgnoreCase);
        foreach(var p in Process.GetProcesses())
            using(p)
            {
                if(p.Id is 0 or 4 || p.Id==Environment.ProcessId || ProgramOf(p.Id) is not { } path) continue;
                bool window; string? title;
                try { window=p.MainWindowHandle!=0; title=window && p.MainWindowTitle.Length>0?p.MainWindowTitle:null; }
                catch(InvalidOperationException) { continue; }
                if(found.TryGetValue(path,out var known)) { found[path]=known with { Count=known.Count+1,Pid=window && !known.Window?p.Id:known.Pid,Window=known.Window||window,Title=known.Title ?? title }; continue; }
                found[path]=new(FromFile(path) ?? new() { Name=p.ProcessName,Path=path },p.Id,1,window,title);
            }
        return found.Values.OrderByDescending(r=>r.Window).ThenBy(r=>r.App.Name,StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // The program file of a running process (null when it cannot be read).
    public static string? ProgramOf(int pid)
    {
        nint process=OpenProcess(0x1000,false,(uint)pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if(process==0) return null;
        try
        {
            var text=new StringBuilder(1024); int length=text.Capacity;
            return QueryFullProcessImageNameW(process,0,text,ref length)?text.ToString():null;
        }
        finally { CloseHandle(process); }
    }
    // The program file at the start of a command line, when it is a full path ("C:\Program Files\x\x.exe" -arg, quoted or not).
    public static string? ProgramOf(string? commandLine)
    {
        if(string.IsNullOrWhiteSpace(commandLine)) return null;
        string line=commandLine.TrimStart(),first;
        if(line.StartsWith('"')) { int close=line.IndexOf('"',1); first=close>0?line[1..close]:line[1..]; }
        else { int exe=line.IndexOf(".exe",StringComparison.OrdinalIgnoreCase); first=exe>0?line[..(exe+4)]:line.Split(' ')[0]; }
        return Path.IsPathFullyQualified(first)?first:null;
    }

    // The folder of an application's program, so that the other programs installed with it are followed too (a launcher's
    // program, a helper started by another process). Null when that folder holds unrelated programs as well: Windows, the
    // Program Files folders themselves, a drive, the user's own folders (Desktop, Downloads, Documents).
    public static string? InstallFolder(string program)
    {
        string? folder=Path.GetDirectoryName(Path.GetFullPath(program));
        if(string.IsNullOrEmpty(folder)) return null;
        folder=Path.TrimEndingDirectorySeparator(folder);
        if(Path.GetPathRoot(folder) is { } root && Path.TrimEndingDirectorySeparator(root).Equals(folder,StringComparison.OrdinalIgnoreCase)) return null;
        string windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows),profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if(windows.Length>0 && PathPolicy.IsUnder(folder,windows)) return null;
        var shared=new[] { Environment.SpecialFolder.ProgramFiles,Environment.SpecialFolder.ProgramFilesX86,Environment.SpecialFolder.CommonProgramFiles,Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.UserProfile,Environment.SpecialFolder.DesktopDirectory,Environment.SpecialFolder.MyDocuments,Environment.SpecialFolder.LocalApplicationData,Environment.SpecialFolder.ApplicationData }
            .Select(Environment.GetFolderPath).Append(profile.Length>0?Path.Combine(profile,"Downloads"):"").Where(f=>f.Length>0);
        return shared.Any(f=>Path.TrimEndingDirectorySeparator(f).Equals(folder,StringComparison.OrdinalIgnoreCase))?null:folder;
    }

    // Most editing applications run once: starting another only hands over to the running one, whose start-up is past.
    public static bool IsRunning(AppLaunch app)
    {
        var running=Process.GetProcessesByName(app.ProcessName);
        foreach(var p in running) p.Dispose();
        return running.Length>0;
    }

    // Starts the application as the signed-in user without administrator rights, also from this elevated process: with the
    // token of the desktop (Explorer), so that it runs as when started from the Start menu and sees the user's network drives.
    // Returns its process id.
    public static int Start(AppLaunch app)
    {
        if(!File.Exists(app.Path)) throw new FileNotFoundException("起動するアプリが見つかりません: "+app.Path);
        string directory=app.WorkingDirectory is { Length:>0 } d && Directory.Exists(d)?d:System.IO.Path.GetDirectoryName(app.Path)!;
        if(!EtwRecorder.IsAdministrator)
        {
            var info=new ProcessStartInfo(app.Path,app.Arguments ?? "") { UseShellExecute=false,WorkingDirectory=directory };
            using var started=Process.Start(info) ?? throw new IOException("アプリを起動できませんでした: "+app.Path);
            return started.Id;
        }
        nint shell=GetShellWindow();
        if(shell==0) throw new InvalidOperationException("Windowsのデスクトップ（エクスプローラー）が見つからないため、ふだんの権限でアプリを起動できません。");
        GetWindowThreadProcessId(shell,out uint shellPid);
        nint process=OpenProcess(0x1000,false,shellPid),token=0,primary=0,environment=0; // PROCESS_QUERY_LIMITED_INFORMATION
        if(process==0) throw new Win32Exception();
        try
        {
            if(!OpenProcessToken(process,0x0002,out token)) throw new Win32Exception(); // TOKEN_DUPLICATE
            // TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID, a primary token
            if(!DuplicateTokenEx(token,0x018B,0,2,1,out primary)) throw new Win32Exception();
            if(!CreateEnvironmentBlock(out environment,primary,false)) environment=0;
            var startup=new StartupInfo { Size=Marshal.SizeOf<StartupInfo>() };
            // The command line buffer may be changed by the call, so it is not a string.
            var commandLine=new StringBuilder("\""+app.Path+"\""+(string.IsNullOrWhiteSpace(app.Arguments)?"":" "+app.Arguments));
            if(!CreateProcessWithTokenW(primary,0,app.Path,commandLine,environment!=0?0x400u:0,environment,directory,ref startup,out var info)) throw new Win32Exception(); // CREATE_UNICODE_ENVIRONMENT
            CloseHandle(info.Thread); CloseHandle(info.Process);
            return (int)info.ProcessId;
        }
        finally
        {
            if(environment!=0) DestroyEnvironmentBlock(environment);
            if(primary!=0) CloseHandle(primary);
            if(token!=0) CloseHandle(token);
            CloseHandle(process);
        }
    }

    [ComImport,Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink { }
    [ComImport,InterfaceType(ComInterfaceType.InterfaceIsIUnknown),Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,int length,nint findData,uint flags);
        void GetIDList(out nint list);
        void SetIDList(nint list);
        void GetDescription([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder name,int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory,int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments,int length);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder path,int length,out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path,int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path,uint reserved);
        void Resolve(nint window,uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        public int Size; public nint Reserved,Desktop,Title; public int X,Y,Width,Height,CountX,CountY,FillAttribute,Flags;
        public short ShowWindow,Reserved2Size; public nint Reserved2,StdInput,StdOutput,StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public nint Process,Thread; public uint ProcessId,ThreadId; }
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint pid);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern nint OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool QueryFullProcessImageNameW(nint process,uint flags,StringBuilder name,ref int length);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool OpenProcessToken(nint process,uint access,out nint token);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool DuplicateTokenEx(nint token,uint access,nint attributes,int level,int type,out nint duplicate);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern bool CreateProcessWithTokenW(nint token,uint logonFlags,string application,StringBuilder commandLine,uint creationFlags,nint environment,string directory,ref StartupInfo startup,out ProcessInformation info);
    [DllImport("userenv.dll",SetLastError=true)] private static extern bool CreateEnvironmentBlock(out nint environment,nint token,bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(nint environment);
}
