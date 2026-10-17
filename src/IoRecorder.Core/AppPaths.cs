using System.Text;
using System.Text.RegularExpressions;

namespace IoRecorder.Core;

// Everything the tools write lives under one folder: Documents\IORecorder. Only dummy files go elsewhere (the test target),
// because they have to sit on the storage being measured. IORECORDER_HOME overrides the root (tests use it).
public static class AppPaths
{
    public static string Root => Environment.GetEnvironmentVariable("IORECORDER_HOME") is { Length:>0 } home
        ? Path.GetFullPath(home)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"IORecorder");
    public static string SettingsFile => Path.Combine(Root,"settings.json");
    public static string Recordings => Path.Combine(Root,"Recordings");
    public static string Benchmarks => Path.Combine(Root,"Benchmarks");
    public static string Configs => Path.Combine(Root,"Configs");
    public static string Verification => Path.Combine(Root,"Verification");
    public static string Demo => Path.Combine(Root,"Demo");
    public static string Temp => Path.Combine(Root,"Temp");

    // "yyyyMMdd-HHmmss_subject": sorts by time, says what it is, and has no spaces (easy to type in a shell).
    public static string FolderName(string subject,DateTime? at=null)
    {
        var clean=Regex.Replace(subject??"","[\\\\/:*?\"<>|\\s]+","-").Trim('-','.');
        clean=new string(clean.Where(c=>!char.IsControl(c)).ToArray());
        if(clean.Length>48) clean=clean[..48].TrimEnd('-','.');
        string stamp=(at??DateTime.Now).ToString("yyyyMMdd-HHmmss");
        return clean.Length>0?stamp+"_"+clean:stamp;
    }
    // A new, not yet existing folder below parent; a numeric suffix keeps two runs in the same second apart.
    public static string NewFolder(string parent,string subject)
    {
        string name=FolderName(subject),path=Path.Combine(parent,name);
        for(int i=2;Directory.Exists(path) || File.Exists(path);i++) path=Path.Combine(parent,name+"-"+i);
        return path;
    }
    // Copies bundled sample configurations into Configs once, so they can be edited without touching the program folder.
    public static void EnsureSampleConfigs(string samples)
    {
        if(!Directory.Exists(samples)) return;
        Directory.CreateDirectory(Configs);
        foreach(var file in Directory.EnumerateFiles(samples,"*.json"))
        {
            var copy=Path.Combine(Configs,Path.GetFileName(file));
            if(!File.Exists(copy)) File.Copy(file,copy);
        }
    }
}
