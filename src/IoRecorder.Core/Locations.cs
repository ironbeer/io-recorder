using System.Text.Json;
using System.Text.Json.Serialization;

namespace IoRecorder.Core;

// The folders a record is narrowed to: when it is viewed (02), when a dummy is made from it and replayed (03) and when it is
// compared with a replay (05). Kept in the record (locations.json) so that all of them use the same; no folders means every
// location. A folder is taken with everything in it; "<folder>\*" takes only what is directly in that folder (not its
// folders). I/O whose path is not known is outside any narrowing.
public sealed class LocationFilter
{
    public const string FileName = "locations.json";
    public const string DirectSuffix = "\\*";
    private string[]? _prefixes;
    private bool[]? _direct;
    public List<string> Folders { get; set; } = [];
    [JsonIgnore] public bool IsEmpty => Folders.Count == 0;
    // The same folders give the same key, whatever their order or letter case.
    [JsonIgnore] public string Key => string.Join("|", Folders.Select(f => f.ToUpperInvariant()).Order(StringComparer.Ordinal));
    public bool Includes(string path)
    {
        if (Folders.Count == 0) return true;
        if (string.IsNullOrEmpty(path)) return false;
        if (_prefixes is null) { _direct = Folders.Select(f => f.EndsWith(DirectSuffix, StringComparison.Ordinal)).ToArray(); _prefixes = Folders.Select((f, i) => _direct[i] ? f[..^1] : f + "\\").ToArray(); }
        for (int i = 0; i < _prefixes.Length; i++)
        {
            string prefix = _prefixes[i];
            if (_direct![i]) { if (path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path.IndexOf('\\', prefix.Length) < 0) return true; }
            else if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || path.Length == prefix.Length - 1 && prefix.StartsWith(path, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    public string Describe() => Folders.Count switch { 0 => "すべての場所", 1 => Display(Folders[0]), _ => $"{Display(Folders[0])} ほか {Folders.Count - 1} か所" };
    // All the folders as one line, the ones taken without their folders marked.
    [JsonIgnore] public string Text => Folders.Count == 0 ? "すべての場所" : string.Join(" / ", Folders.Select(Display));
    public static string TextOf(IEnumerable<string> folders) => Of(folders).Text;
    private static string Display(string folder) => folder.EndsWith(DirectSuffix, StringComparison.Ordinal) ? folder[..^1] + "（直下のファイルだけ）" : folder;

    // Folders without a trailing separator, each once, without one that is inside another.
    public static LocationFilter Of(IEnumerable<string> folders)
    {
        var list = folders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim().Replace('/', '\\').TrimEnd('\\'))
            .Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new() { Folders = list.Where(f => !list.Any(o => !o.Equals(f, StringComparison.OrdinalIgnoreCase) && PathPolicy.IsUnder(f, o))).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList() };
    }
    public static LocationFilter Load(string session)
    {
        string file = Path.Combine(session, FileName);
        if (!File.Exists(file)) return new();
        try { return Of(JsonStore.Read<LocationFilter>(file).Folders); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException) { return new(); }
    }
    public static void Save(string session, LocationFilter filter)
    {
        string file = Path.Combine(session, FileName);
        if (filter.IsEmpty) { if (File.Exists(file)) File.Delete(file); }
        else JsonStore.Write(file, filter);
    }
}

// A summary of a record narrowed to some folders (analysis-locations.json), reused while the record and the folders stay the same.
public sealed class NarrowedAnalysis
{
    public const string FileName = "analysis-locations.json", ReportName = "report-locations.html";
    public string Key { get; set; } = "";
    public AnalysisResult Result { get; set; } = new();
}
