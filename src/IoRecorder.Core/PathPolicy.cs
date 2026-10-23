using System.Text;
using System.Runtime.InteropServices;

namespace IoRecorder.Core;

public static class PathPolicy
{
    public static string NormalizeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("フォルダはドライブ文字またはUNCから始まる絶対パスで指定してください。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Replace('/', '\\')));
    }

    public static bool IsUnder(string path, string root)
    {
        var p = path.Replace('/', '\\').TrimEnd('\\');
        var r = root.Replace('/', '\\').TrimEnd('\\');
        return p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
    }

    // The drive or share a path is on: "C:", "\\server\share", the device of a path no drive letter maps to, or "" when the
    // path is not known. Called for every left-out event, so a drive path allocates nothing.
    private static readonly string[] Drives = Enumerable.Range('A', 26).Select(c => (char)c + ":").ToArray();
    public static string VolumeOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0])) return Drives[char.ToUpperInvariant(path[0]) - 'A'];
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = path[2..].Split('\\', 3);
            return parts.Length >= 2 ? @"\\" + parts[0] + @"\" + parts[1] : @"\\" + parts[0];
        }
        if (path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)) { int second = path.IndexOf('\\', 8); return second < 0 ? path : path[..second]; }
        return path.Split('\\')[0];
    }

    public static string DatasetRelative(string source)
    {
        var path = source.Replace('/', '\\');
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) path = path[4..];
        string prefix;
        if (path.Length > 2 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\')
        { prefix = char.ToUpperInvariant(path[0]).ToString(); path = path[3..]; }
        else if (path.StartsWith(@"\\")) { prefix = "UNC"; path = path[2..]; }
        else throw new InvalidDataException($"再生できないデバイスパスです: {source}");
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.EndsWith('.') || part.EndsWith(' '))
                throw new InvalidDataException($"安全に変換できないファイル名です: {source}");
            var baseName = part.Split('.')[0];
            if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) || baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) || baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || System.Text.RegularExpressions.Regex.IsMatch(baseName, "^(COM|LPT)[1-9]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidDataException($"予約名は再生できません: {source}");
        }
        return Path.Combine([prefix, .. parts]);
    }

    // String-only check: never touches the file system, so it is safe to call for every replayed request.
    public static string CombineInside(string dataset, string relative)
    {
        var root = Path.GetFullPath(dataset);
        if (Path.IsPathRooted(relative) || relative.Split('\\','/').Any(p => p is "." or "..") || relative.Contains(':'))
            throw new InvalidDataException("データセット外へのパス参照を拒否しました。");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsUnder(full, root) || full.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("データセット外へのアクセスを拒否しました。");
        return full;
    }

    // CombineInside without the exception: false for a path that would leave the dataset.
    public static bool TryCombineInside(string dataset, string relative, out string full)
    {
        try { full = CombineInside(dataset, relative); return true; }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { full = ""; return false; }
    }

    // CombineInside plus a check of every existing folder on the way for junctions and symbolic links. It queries the file
    // system several times per call: use it before a run, never per replayed request (it would add load to the measured storage).
    public static string ResolveInside(string dataset, string relative)
    {
        var root = Path.GetFullPath(dataset);
        var full = CombineInside(dataset, relative);
        RejectReparsePoint(root);
        if (UnsafeInside(root, [relative]).Count > 0) throw new IOException($"ジャンクション・シンボリックリンクを経由するパスは使えません: {full}");
        return full;
    }

    // Dataset paths that would leave the dataset or pass through a junction or symbolic link inside it. Each file and folder
    // is checked once (before a run, never per request); the caller skips these paths instead of stopping.
    public static HashSet<string> UnsafeInside(string dataset, IEnumerable<string> relatives)
    {
        var root = Path.GetFullPath(dataset); var top = Path.TrimEndingDirectorySeparator(root);
        var links = new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in relatives)
        {
            if (!TryCombineInside(root, relative, out var full)) { found.Add(relative); continue; }
            for (var current = full; current is not null && !current.Equals(top, StringComparison.OrdinalIgnoreCase) && IsUnder(current, root); current = Path.GetDirectoryName(current))
            {
                if (!links.TryGetValue(current, out var link)) links[current] = link = IsReparsePoint(current);
                if (link) { found.Add(relative); break; }
            }
        }
        return found;
    }

    // The dataset folder itself is created by this tool and must be a plain folder. Folders above it may be links (a redirected
    // or mounted location): they cannot lead writes outside the dataset, so they are not checked.
    public static void RejectReparsePoint(string path)
    {
        if (IsReparsePoint(Path.GetFullPath(path)))
            throw new IOException($"ダミーのフォルダがジャンクション・シンボリックリンクになっています: {path}");
    }
    // One question per path, on the storage being tested and for every path of a dummy before each run: asking whether it is a
    // file, then a folder, then for its attributes made two or three requests of each path (and two "not found" of a missing one,
    // which went into the recordings of replays and into records of benchmarks).
    private static bool IsReparsePoint(string path)
    {
        uint attributes = GetFileAttributes(Long(path));
        return attributes != uint.MaxValue && (attributes & (uint)FileAttributes.ReparsePoint) != 0;
    }
    // A full path in the form the file APIs take at any length (\\?\C:\…, \\?\UNC\server\share\…).
    internal static string Long(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributes(string path);
}

public sealed class DevicePathMapper
{
    private readonly List<(string Device,string Drive)> _devices = [];
    public DevicePathMapper()
    {
        foreach (var drive in Environment.GetLogicalDrives())
        {
            var buffer = new StringBuilder(32768);
            if (QueryDosDevice(drive[..2], buffer, buffer.Capacity) != 0)
                _devices.Add((buffer.ToString().Split('\0')[0], drive[..2]));
        }
        _devices.Sort((a,b) => b.Device.Length.CompareTo(a.Device.Length));
    }
    public string Map(string path)
    {
        if (path.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        if (path.StartsWith(@"\??\")) return path[4..];
        // A file on a share, also opened through a drive letter (\Device\Mup\;LanmanRedirector\;Z:…\server\share\…).
        if (NetworkDrives.UncOf(path) is { } unc) return unc;
        foreach (var (device, drive) in _devices)
            if (PathPolicy.IsUnder(path, device)) return drive + path[device.Length..];
        return path;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string name, StringBuilder target, int size);
}

public static class RootResolver
{
    // Per-sign-in drive letters (a network drive mapped in Explorer) are not there after elevation: the share of such a drive
    // is found from the user's side (NetworkDrives), so a folder on it is matched by its UNC path.
    public static ResolvedRoot Resolve(string root, string? devicePath = null)
    {
        root = PathPolicy.NormalizeRoot(root);
        var result = new ResolvedRoot { UserPath = root, DevicePath = string.IsNullOrWhiteSpace(devicePath) ? null : devicePath.TrimEnd('\\') };
        if (root.Length < 2 || root[1] != ':') return result;
        string drive = root[..2], rest = root.Length > 3 ? root[2..] : "";
        var target = new StringBuilder(32768);
        if (result.DevicePath is null && QueryDosDevice(drive, target, target.Capacity) != 0)
        {
            var device = target.ToString().Split('\0')[0];
            if (NetworkDrives.UncOf(device) is { } share) result.UncPath = share.TrimEnd('\\') + rest;
            else if (!device.StartsWith(@"\??\")) result.DevicePath = device + rest;
        }
        int length = 1024; var remote = new StringBuilder(length);
        if (WNetGetConnection(drive, remote, ref length) == 0 && remote.Length > 0) result.UncPath = remote.ToString().TrimEnd('\\') + rest;
        if (result.UncPath is null && result.DevicePath is null && NetworkDrives.Accessible(root) is var unc && unc != root) result.UncPath = unc;
        return result;
    }
    // A drive letter that is neither a drive of this process nor a network drive of the user (a typing slip, a drive taken off).
    public static bool Unknown(ResolvedRoot root) =>
        root.UncPath is null && root.DevicePath is null && root.UserPath.Length > 1 && root.UserPath[1] == ':' && QueryDosDevice(root.UserPath[..2], new StringBuilder(1024), 1024) == 0;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string name, StringBuilder target, int size);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
}

// Matches ETW paths against roots without allocating; matched paths are rewritten to the user's spelling of the root.
public sealed class RootMatcher
{
    private readonly (string Prefix, string Display)[] _mapped, _raw;
    public RootMatcher(IEnumerable<ResolvedRoot> roots)
    {
        var mapped = new List<(string, string)>(); var raw = new List<(string, string)>();
        foreach (var r in roots)
        {
            var user = r.UserPath.TrimEnd('\\');
            mapped.Add((user, user));
            if (!string.IsNullOrWhiteSpace(r.UncPath)) mapped.Add((r.UncPath.TrimEnd('\\'), user));
            if (!string.IsNullOrWhiteSpace(r.DevicePath)) raw.Add((r.DevicePath.TrimEnd('\\'), user));
        }
        _mapped = [.. mapped]; _raw = [.. raw];
    }
    public bool Match(string raw, string mapped, out string display)
    {
        display = mapped;
        foreach (var (prefix, user) in _mapped)
            if (Under(mapped, prefix)) { if (!ReferenceEquals(prefix, user)) display = user + mapped[prefix.Length..]; return true; }
        foreach (var (prefix, user) in _raw)
            if (Under(raw, prefix)) { display = user + raw[prefix.Length..]; return true; }
        return false;
    }
    public static bool Under(ReadOnlySpan<char> path, string root) =>
        root.Length > 0 && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && (path.Length == root.Length || path[root.Length] == '\\');
}
