using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace IoRecorder.Core;

// A network drive of the signed-in user (Z: → \\server\share) and where the tool found it.
public sealed record NetworkDrive(string Drive, string Share, string Source);

// Windows keeps drive letters per sign-in, and the administrator side of the same user (this tool) has its own: a drive mapped
// in Explorer (Z: → \\server\share) is not there, so Z:\… can be neither opened nor matched from here. The share behind the
// letter is found in this process, from the user's side (Explorer's token) or in the list Windows keeps of the drives it
// reconnects at sign-in (HKCU\Network, read only), and the tool uses \\server\share itself. Nothing is changed on the machine.
public static class NetworkDrives
{
    public const string Here = "このプロセス", User = "ふだんの権限", Reconnected = "サインイン時に再接続する一覧";
    private static readonly string[] Letters = Enumerable.Range('A', 26).Select(c => (char)c + ":").ToArray();

    public static List<NetworkDrive> List()
    {
        var found = new Dictionary<string, NetworkDrive>(StringComparer.OrdinalIgnoreCase);
        // A letter taken by a local drive on one side is that drive, whatever an older list says about it.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Look(string source)
        {
            foreach (var drive in Letters)
                if (!taken.Contains(drive) && DosDevice(drive) is { } device)
                {
                    taken.Add(drive);
                    if (UncOf(device) is { } share) found[drive] = new(drive, share.TrimEnd('\\'), source);
                }
        }
        Look(Here);
        if (EtwRecorder.IsAdministrator) AppLauncher.AsDesktopUser(() => Look(User));
        try
        {
            using var network = Registry.CurrentUser.OpenSubKey("Network");
            foreach (var letter in network?.GetSubKeyNames() ?? [])
            {
                using var key = network!.OpenSubKey(letter);
                string drive = letter.ToUpperInvariant() + ":";
                if (letter.Length == 1 && char.IsAsciiLetter(letter[0]) && !taken.Contains(drive) && key?.GetValue("RemotePath") is string { Length: > 2 } remote && remote.StartsWith(@"\\", StringComparison.Ordinal))
                    found[drive] = new(drive, remote.TrimEnd('\\'), Reconnected);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return found.Values.OrderBy(d => d.Drive, StringComparer.Ordinal).ToList();
    }

    // The path to open from this process: Z:\… becomes \\server\share\… when Z: is not a drive here but a network drive of the
    // user. Anything else is returned as it is.
    public static string Accessible(string path) => Accessible(path, Elsewhere());
    // The same for many paths, with the drives listed once.
    public static string Accessible(string path, IReadOnlyCollection<NetworkDrive> elsewhere)
    {
        if (path.Length < 2 || path[1] != ':') return path;
        foreach (var d in elsewhere)
            if (char.ToUpperInvariant(path[0]) == d.Drive[0]) return d.Share + path[2..];
        return path;
    }
    // The network drives that are no drives of this process (List never gives a letter taken here to another source).
    public static List<NetworkDrive> Elsewhere() => List().Where(d => d.Source != Here).ToList();

    // A folder given as a share, written with the drive letter the user knows it by (\\server\share\x → Z:\x).
    public static string ToDrive(string path, IEnumerable<NetworkDrive> drives)
    {
        foreach (var d in drives.OrderByDescending(d => d.Share.Length))
            if (RootMatcher.Under(path, d.Share)) return d.Drive + (path.Length == d.Share.Length ? @"\" : path[d.Share.Length..]);
        return path;
    }

    // The UNC form of a network device path: a mapped drive's device ("\Device\LanmanRedirector\;Z:0000000000012345\server\share",
    // also behind "\Device\Mup\;LanmanRedirector\…"), a path through the network's multiple UNC provider ("\Device\Mup\server\
    // share\…") or "\??\UNC\server\share". The parts starting with ';' are the provider's and the sign-in's, never a server name.
    // Null for anything else (a local volume).
    public static string? UncOf(string? device)
    {
        if (string.IsNullOrEmpty(device)) return null;
        if (device.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + device[8..];
        if (!device.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)) return null;
        int provider = device.IndexOf('\\', 8);
        if (provider < 0) return null;
        bool mup = device.AsSpan(8, provider - 8).Equals("Mup", StringComparison.OrdinalIgnoreCase), redirected = false;
        int i = provider + 1;
        while (i < device.Length && device[i] == ';')
        {
            int next = device.IndexOf('\\', i);
            if (next < 0) return null;
            i = next + 1; redirected = true;
        }
        return (mup || redirected) && i < device.Length ? @"\\" + device[i..] : null;
    }

    // \\server\share\ of a path on a share (with the ending backslash the volume calls want).
    public static string ShareRoot(string unc)
    {
        var parts = unc.TrimStart('\\').Split('\\');
        return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}\" : unc;
    }

    private static string? DosDevice(string drive)
    {
        var target = new StringBuilder(1024);
        return QueryDosDevice(drive, target, target.Capacity) != 0 ? target.ToString().Split('\0')[0] : null;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string name, StringBuilder target, int size);
}
